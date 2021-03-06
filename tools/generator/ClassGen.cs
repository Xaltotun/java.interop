using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using Mono.Cecil;

using Java.Interop.Tools.TypeNameMappings;

using Xamarin.Android.Binder;
using Xamarin.Android.Tools;

using MonoDroid.Utils;

namespace MonoDroid.Generation {
#if USE_CECIL
	static class ManagedExtensions
	{
		public static string FullNameCorrected (this TypeReference t)
		{
			return t.FullName.Replace ('/', '.');
		}
	}
	
	public class ManagedClassGen : ClassGen {
		TypeDefinition t;
		TypeReference nominal_base_type;
		
		public ManagedClassGen (TypeDefinition t)
			: base (new ManagedGenBaseSupport (t))
		{
			this.t = t;
			foreach (var itd in t.Interfaces) {
				var def = itd.Resolve ();
				if (def != null && def.IsNotPublic)
					continue;
				AddInterface (itd.FullNameCorrected ());
			}
			bool implements_charsequence = t.Interfaces.Any (it => it.FullName == "Java.Lang.CharSequence");
			foreach (var m in t.Methods) {
				if (m.IsPrivate || m.IsAssembly || !m.CustomAttributes.Any (ca => ca.AttributeType.FullNameCorrected () == "Android.Runtime.RegisterAttribute"))
					continue;
				if (implements_charsequence && t.Methods.Any (mm => mm.Name == m.Name + "Formatted"))
					continue;
				if (m.IsConstructor)
					Ctors.Add (new ManagedCtor (this, m));
				else
					AddMethod (new ManagedMethod (this, m));
			}
			foreach (var f in t.Fields)
				if (!f.IsPrivate && !f.CustomAttributes.Any (ca => ca.AttributeType.FullNameCorrected () == "Android.Runtime.RegisterAttribute"))
					AddField (new ManagedField (f));
			for (nominal_base_type = t.BaseType; nominal_base_type != null && (nominal_base_type.HasGenericParameters || nominal_base_type.IsGenericInstance); nominal_base_type = nominal_base_type.Resolve ().BaseType)
				; // iterate up to non-generic type, at worst System.Object.
		}
		
		public override string BaseType {
			get { return nominal_base_type != null ? nominal_base_type.FullNameCorrected () : null; }
		}

		public override bool IsAbstract {
			get { return t.IsAbstract; }
		}

		public override bool IsFinal {
			get { return t.IsSealed; }
		}
	}
#endif
	
	public class XmlClassGen : ClassGen {
		bool is_abstract;
		bool is_final;
		string base_type;

		public XmlClassGen (XmlElement pkg, XmlElement elem)
			: base (new XmlGenBaseSupport (pkg, elem))//FIXME: should not be xml specific
		{
			is_abstract = elem.XGetAttribute ("abstract") == "true";
			is_final = elem.XGetAttribute ("final") == "true";
			base_type = elem.XGetAttribute ("extends");
			foreach (XmlNode node in elem.ChildNodes) {
				XmlElement child = node as XmlElement;
				if (child == null)
					continue;
				switch (node.Name) {
				case "implements":
					string iname = child.XGetAttribute ("name-generic-aware");
					iname = iname.Length > 0 ? iname : child.XGetAttribute ("name");
					AddInterface (iname);
					break;
				case "method":
					var synthetic = child.GetAttribute ("synthetic") == "true";
					var finalizer = child.GetAttribute ("name") == "finalize" &&
						child.GetAttribute ("jni-signature") == "()V";
					if (!(synthetic || finalizer))
						AddMethod (new XmlMethod (this, child));
					break;
				case "constructor":
					Ctors.Add (new XmlCtor (this, child));
					break;
				case "field":
					AddField (new XmlField (child));
					break;
				case "typeParameters":
					break; // handled at GenBaseSupport
				default:
					Report.Warning (0, Report.WarningClassGen + 1, "unexpected class child {0}.", node.Name);
					break;
				}
			}
		}
		
		public override bool IsAbstract {
			get { return is_abstract; }
		}
		
		public override bool IsFinal {
			get { return is_final; }
		}
		
		public override string BaseType {
			get { return base_type; }
		}
	}

	public abstract class ClassGen : GenBase {

		bool needs_new;
		bool inherits_object;
		List<Ctor> ctors = new List<Ctor> ();
		List<string> explicitly_implemented_iface_methods = new List<string> (); // do not initialize here; see FixupMethodOverides()
		bool fill_explicit_implementation_started;

		protected ClassGen (GenBaseSupport support)
			: base (support)
		{
			inherits_object = true;
			if (Namespace == "Java.Lang" && (Name == "Object" || Name == "Throwable"))
				inherits_object = false;
		}

		public override string DefaultValue {
			get { return "IntPtr.Zero"; }
		}

		internal    bool    InheritsObject {
			get { return inherits_object; }
		}
		
		public List<string> ExplicitlyImplementedInterfaceMethods {
			get { return explicitly_implemented_iface_methods; }
		}

		public abstract bool IsAbstract { get; }

		public abstract bool IsFinal { get; }

		public override string NativeType {
			get { return "IntPtr"; }
		}

		public bool HasClassHandle {
			get { return ctors.Count > 0 || Fields.Count > 0 || Methods.Count > 0 || Properties.Count > 0 || IsAnnotation; }
		}

		public IList<Ctor> Ctors {
			get { return ctors; }
		}

		public abstract string BaseType { get; }

		public bool ContainsCtor (string jni_sig)
		{
			foreach (Ctor c in ctors)
				if (c.JniSignature == jni_sig)
					return true;
			return false;
		}

		public bool ContainsNestedType (GenBase gen)
		{
			if (BaseGen != null && BaseGen.ContainsNestedType (gen))
				return true;

			return HasNestedType (gen.Name);
		}

		public override string ToNative (CodeGenerationOptions opt, string varname, Dictionary<string, string> mappings = null) 
		{
			return String.Format ("JNIEnv.ToLocalJniHandle ({0})", varname);
		}

		public override string FromNative (CodeGenerationOptions opt, string varname, bool owned) 
		{
			return String.Format ("global::Java.Lang.Object.GetObject<{0}> ({1}, {2})", opt.GetOutputName (FullName), varname, owned ? "JniHandleOwnership.TransferLocalRef" : "JniHandleOwnership.DoNotTransfer");
		}

		public override void ResetValidation ()
		{
			validated = false;
			base.ResetValidation ();
		}

		protected override bool OnValidate (CodeGenerationOptions opt, GenericParameterDefinitionList type_params)
		{
			if (validated)
				return is_valid;
			
			validated = true;

			// We're validating this in prior to BaseType.
			if (TypeParameters != null && !TypeParameters.Validate (opt, type_params))
				return false;

			if (Char.IsNumber (Name [0])) {
				// it is an anonymous class which does not need output.
				is_valid = false;
				return false;
			}
			
			base_symbol = IsAnnotation ? SymbolTable.Lookup ("java.lang.Object") : BaseType != null ? SymbolTable.Lookup (BaseType) : null;
			if (base_symbol == null && FullName != "Java.Lang.Object" && FullName != "System.Object") {
				Report.Warning (0, Report.WarningClassGen + 2, "Class {0} has unknown base type {1}.", FullName, BaseType);
				is_valid = false;
				return false;
			}

			if ((base_symbol != null && !base_symbol.Validate (opt, TypeParameters)) || !base.OnValidate (opt, type_params)) {
				Report.Warning (0, Report.WarningClassGen + 3, "Class {0} has invalid base type {1}.", FullName, BaseType);
				is_valid = false;
				return false;
			}

			List<Ctor> valid_ctors = new List<Ctor> ();
			foreach (Ctor c in ctors)
				if (c.Validate (opt, TypeParameters))
					valid_ctors.Add (c);
			ctors = valid_ctors;

			return true;
		}
		
		public override void FixupExplicitImplementation ()
		{
			if (fill_explicit_implementation_started)
				return; // already done.
			fill_explicit_implementation_started = true;
			if (BaseGen != null && BaseGen.explicitly_implemented_iface_methods == null)
				BaseGen.FixupExplicitImplementation ();

			foreach (InterfaceGen iface in GetAllDerivedInterfaces ()) {
				if (iface.IsGeneric) {
					bool skip = false;
					foreach (ISymbol isym in Interfaces) {
						var gs = isym as GenericSymbol;
						if (gs != null && gs.IsConcrete && gs.Gen == iface)
							skip = true;
					}
					if (skip)
						continue; // we don't handle it here; generic interface methods are generated in different manner.
				}
				if (BaseGen != null && BaseGen.GetAllDerivedInterfaces ().Contains (iface))
					continue; // no need to fill members for already-implemented-in-base-class iface.
				foreach (var m in iface.Methods.Where (m => !ContainsMethod (m, false, false))) {
					string sig = m.GetSignature ();
					bool doExplicitly = false;
					if (IsCovariantMethod (m))
						doExplicitly = true;
					else if (m.IsGeneric)
						doExplicitly = true;
					if (doExplicitly)
						explicitly_implemented_iface_methods.Add (sig);
				}
			}
			
			// Keep in sync with Generate() that generates explicit iface method impl.
			foreach (ISymbol isym in Interfaces) {
				if (isym is GenericSymbol) {
					GenericSymbol gs = isym as GenericSymbol;
					if (gs.IsConcrete) {
						foreach (Method m in gs.Gen.Methods)
							if (m.IsGeneric) {
								explicitly_implemented_iface_methods.Add (m.GetSignature ());
							}
					}
				}
			}

			foreach (var nt in NestedTypes)
				nt.FixupExplicitImplementation ();
		}
		
		void GenConstructors (StreamWriter sw, string indent, CodeGenerationOptions opt)
		{
			if (FullName != "Java.Lang.Object" && inherits_object) {
				sw.WriteLine ("{0}{1} {2} (IntPtr javaReference, JniHandleOwnership transfer) : base (javaReference, transfer) {{}}", indent, IsFinal ? "internal" : "protected", Name);
				sw.WriteLine ();
			}

			foreach (Ctor ctor in ctors) {
				if (IsFinal && ctor.Visibility == "protected")
					return;
				ctor.Name = Name;
				ctor.Generate (sw, indent, opt, inherits_object, this);
			}
		}

		public override ClassGen BaseGen {
			get { return (base_symbol is GenericSymbol ? (base_symbol as GenericSymbol).Gen : base_symbol) as ClassGen; }
		}

		void GenerateAbstractMembers (StreamWriter sw, string indent, CodeGenerationOptions opt)
		{
			foreach (InterfaceGen gen in GetAllDerivedInterfaces ())
				// FIXME: this is an ugly workaround for bug in generator that generates extraneous member.
				if (FullName != "Android.Views.Animations.BaseInterpolator" || gen.FullName != "Android.Views.Animations.IInterpolator")
					gen.GenerateAbstractMembers (this, sw, indent, opt);
		}

		void GenMethods (StreamWriter sw, string indent, CodeGenerationOptions opt)
		{
			// This does not exclude overrides (unlike virtual methods) because we're not sure
			// if calling the base interface default method via JNI expectedly dispatches to
			// the derived method.
			var defaultMethods = GetAllDerivedInterfaces ()
				.SelectMany (i => i.Methods)
				.Where (m => m.IsInterfaceDefaultMethod)
				.Where (m => !ContainsMethod (m, false, false));
			var overrides = defaultMethods.Where (m => m.IsInterfaceDefaultMethodOverride);

			var overridens = defaultMethods.Where (m => overrides.Where (_ => _.Name == m.Name && _.JniSignature == m.JniSignature)
				.Any (mm => mm.DeclaringType.GetAllDerivedInterfaces ().Contains (m.DeclaringType)));

			foreach (Method m in Methods.Concat (defaultMethods.Except (overridens))) {
				bool virt = m.IsVirtual;
				m.IsVirtual = !IsFinal && virt;
				if (m.IsAbstract && !m.IsInterfaceDefaultMethodOverride && !m.IsInterfaceDefaultMethod)
					m.GenerateAbstractDeclaration (sw, indent, opt, null, this);
				else
					m.Generate (sw, indent, opt, this, true);
				m.IsVirtual = virt;
			}

			var methods = Methods.Concat (Properties.Where (p => p.Setter != null).Select (p => p.Setter));
			foreach (InterfaceGen type in methods.Where (m => m.IsListenerConnector && m.EventName != String.Empty).Select (m => m.ListenerType).Distinct ()) {
				sw.WriteLine ("#region \"Event implementation for {0}\"", type.FullName);
				type.GenerateEventsOrPropertiesForListener (sw, indent, opt, this);
				sw.WriteLine ("#endregion");
			}
		}

		void GenProperties (StreamWriter sw, string indent, CodeGenerationOptions opt)
		{
			foreach (Property prop in Properties) {
				bool get_virt = prop.Getter.IsVirtual;
				bool set_virt = prop.Setter == null ? false : prop.Setter.IsVirtual;
				prop.Getter.IsVirtual = !IsFinal && get_virt;
				if (prop.Setter != null)
					prop.Setter.IsVirtual = !IsFinal && set_virt;
				if (prop.Getter.IsAbstract)
					prop.GenerateAbstractDeclaration (sw, indent, opt, this);
				else
					prop.Generate (this, sw, indent, opt);
				prop.Getter.IsVirtual = get_virt;
				if (prop.Setter != null)
					prop.Setter.IsVirtual = set_virt;
			}
		}

		public override void Generate (StreamWriter sw, string indent, CodeGenerationOptions opt, GenerationInfo gen_info)
		{
			opt.ContextTypes.Push (this);
			gen_info.TypeRegistrations.Add (new KeyValuePair<string, string>(RawJniName, AssemblyQualifiedName));
			bool is_enum = base_symbol != null && base_symbol.FullName == "Java.Lang.Enum";
			if (is_enum)
				gen_info.Enums.Add (RawJniName.Replace ('/', '.') + ":" + Namespace + ":" + JavaSimpleName);
			StringBuilder sb = new StringBuilder ();
			foreach (ISymbol isym in Interfaces) {
				GenericSymbol gs = isym as GenericSymbol;
				InterfaceGen gen = (gs == null ? isym : gs.Gen) as InterfaceGen;
				if (gen != null && gen.IsConstSugar)
					continue;
				if (sb.Length > 0)
					sb.Append (", ");
				sb.Append (opt.GetOutputName (isym.FullName));
			}

			string obj_type = null;
			if (base_symbol != null) {
				GenericSymbol gs = base_symbol as GenericSymbol;
				obj_type = gs != null && gs.IsConcrete ? gs.GetGenericType (null) : opt.GetOutputName (base_symbol.FullName);
			}

			sw.WriteLine ("{0}// Metadata.xml XPath class reference: path=\"{1}\"", indent, MetadataXPathReference);

			if (this.IsDeprecated)
				sw.WriteLine ("{0}[ObsoleteAttribute (@\"{1}\")]", indent, this.DeprecatedComment);
			sw.WriteLine ("{0}[global::Android.Runtime.Register (\"{1}\", DoNotGenerateAcw=true{2})]", indent, RawJniName, this.AdditionalAttributeString ());
			if (this.TypeParameters != null && this.TypeParameters.Any ())
				sw.WriteLine ("{0}{1}", indent, TypeParameters.ToGeneratedAttributeString ());
			string inherits = "";
			if (inherits_object && obj_type != null) {
				inherits  = ": " + obj_type;
			}
			if (sb.Length > 0) {
				if (string.IsNullOrEmpty (inherits))
					inherits = ": ";
				else
					inherits += ", ";
			}
			sw.WriteLine ("{0}{1} {2}{3}{4}partial class {5} {6}{7} {{",
					indent,
					Visibility,
					needs_new ? "new " : String.Empty,
					IsAbstract ? "abstract " : String.Empty,
					IsFinal ? "sealed " : String.Empty,
					Name,
					inherits,
					sb.ToString ());
			sw.WriteLine ();

			var seen = new HashSet<string> ();
			GenFields (sw, indent + "\t", opt, seen);
			bool haveNested = false;
			foreach (var iface in GetAllImplementedInterfaces ()
					.Except (BaseGen == null
						? new InterfaceGen[0]
						: BaseGen.GetAllImplementedInterfaces ())
					.Where (i => i.Fields.Count > 0)) {
				if (!haveNested) {
					sw.WriteLine ();
					sw.WriteLine ("{0}\tpublic static class InterfaceConsts {{", indent);
					haveNested = true;
				}
				sw.WriteLine ();
				sw.WriteLine ("{0}\t\t// The following are fields from: {1}", indent, iface.JavaName);
				iface.GenFields (sw, indent + "\t\t", opt, seen);
			}

			if (haveNested)
				sw.WriteLine ("{0}\t}}\n", indent);

			foreach (GenBase nest in NestedTypes) {
				if (BaseGen != null && BaseGen.ContainsNestedType (nest))
					if (nest is ClassGen)
						(nest as ClassGen).needs_new = true;
				nest.Generate (sw, indent + "\t", opt, gen_info);
				sw.WriteLine ();
			}

			if (HasClassHandle) {
				bool requireNew = false;
				for (var bg = BaseGen; bg != null && bg is XmlClassGen; bg = bg.BaseGen) {
					if (bg.HasClassHandle) {
						requireNew = true;
						break;
					}
				}

				opt.CodeGenerator.WriteClassHandle (this, sw, indent, opt, requireNew);
			}

			GenConstructors (sw, indent + "\t", opt);

			GenProperties (sw, indent + "\t", opt);
			GenMethods (sw, indent + "\t", opt);

			if (IsAbstract)
				GenerateAbstractMembers (sw, indent + "\t", opt);

			bool is_char_seq = false;
			foreach (ISymbol isym in Interfaces) {
				if (isym is GenericSymbol) {
					GenericSymbol gs = isym as GenericSymbol;
					if (gs.IsConcrete) {
						// FIXME: not sure if excluding default methods is a valid idea...
						foreach (Method m in gs.Gen.Methods.Where (m => !m.IsInterfaceDefaultMethod && !m.IsStatic))
							if (m.IsGeneric)
								m.GenerateExplicitIface (sw, indent + "\t", opt, gs);
					}
				} else if (isym.FullName == "Java.Lang.ICharSequence")
					is_char_seq = true;
			}

			if (is_char_seq)
				GenCharSequenceEnumerator (sw, indent + "\t", opt);

			sw.WriteLine (indent + "}");

			if (!AssemblyQualifiedName.Contains ('/')) {
				foreach (InterfaceExtensionInfo nestedIface in GetNestedInterfaceTypes ())
					nestedIface.Type.GenerateExtensionsDeclaration (sw, indent, opt, nestedIface.DeclaringType);
			}

			if (IsAbstract) {
				sw.WriteLine ();
				GenerateInvoker (sw, indent, opt);
			}
			opt.ContextTypes.Pop ();
		}

		class InterfaceExtensionInfo {
			public string DeclaringType;
			public InterfaceGen Type;
		}

		IEnumerable<InterfaceExtensionInfo> GetNestedInterfaceTypes ()
		{
			var nestedInterfaces = new List<InterfaceExtensionInfo> ();
			AddNestedInterfaceTypes (this, nestedInterfaces);
			return nestedInterfaces;
		}

		static void AddNestedInterfaceTypes (GenBase type, List<InterfaceExtensionInfo> nestedInterfaces)
		{
			foreach (GenBase nt in type.NestedTypes) {
				InterfaceGen ni = nt as InterfaceGen;
				if (ni != null)
					nestedInterfaces.Add (new InterfaceExtensionInfo {
							DeclaringType = type.FullName.Substring (type.Namespace.Length+1).Replace (".","_"),
							Type          = ni
					});
				else
					AddNestedInterfaceTypes (nt, nestedInterfaces);
			}
		}

		void GenerateInvoker (StreamWriter sw, string indent, CodeGenerationOptions opt)
		{
			StringBuilder sb = new StringBuilder ();
			foreach (InterfaceGen igen in GetAllDerivedInterfaces ())
				if (igen.IsGeneric)
					sb.Append (", " + opt.GetOutputName (igen.FullName));
			sw.WriteLine ("{0}[global::Android.Runtime.Register (\"{1}\", DoNotGenerateAcw=true{2})]", indent, RawJniName, this.AdditionalAttributeString ());
			sw.WriteLine ("{0}internal partial class {1}Invoker : {1}{2} {{", indent, Name, sb.ToString ());
			sw.WriteLine ();
			sw.WriteLine ("{0}\tpublic {1}Invoker (IntPtr handle, JniHandleOwnership transfer) : base (handle, transfer) {{}}", indent, Name);
			sw.WriteLine ();
			opt.CodeGenerator.WriteClassInvokerHandle (this, sw, indent + "\t", opt, Name + "Invoker");

			HashSet<string> members = new HashSet<string> ();
			GenerateInvokerMembers (sw, indent + "\t", opt, members);
			sw.WriteLine ("{0}}}", indent);
			sw.WriteLine ();
		}

		void GenerateInvokerMembers (StreamWriter sw, string indent, CodeGenerationOptions opt, HashSet<string> members)
		{
			GenerateInvoker (sw, Properties, indent, opt, members);
			GenerateInvoker (sw, Methods, indent, opt, members, null);

			foreach (InterfaceGen iface in GetAllDerivedInterfaces ()) {
//				if (iface.IsGeneric)
//					continue;
				GenerateInvoker (sw, iface.Properties.Where (p => !ContainsProperty (p.Name, false, false)), indent, opt, members);
				GenerateInvoker (sw, iface.Methods.Where (m => !m.IsInterfaceDefaultMethod && !ContainsMethod (m, false, false) && !IsCovariantMethod (m) && !explicitly_implemented_iface_methods.Contains (m.GetSignature ())), indent, opt, members, iface);
			}

			if (BaseGen != null && BaseGen.FullName != "Java.Lang.Object")
				BaseGen.GenerateInvokerMembers (sw, indent, opt, members);
		}

		void GenerateInvoker (StreamWriter sw, IEnumerable<Property> properties, string indent, CodeGenerationOptions opt, HashSet<string> members)
		{
			foreach (Property prop in properties) {
				if (members.Contains (prop.Name))
					continue;
				members.Add (prop.Name);
				if ((prop.Getter != null && !prop.Getter.IsAbstract) ||
						(prop.Setter != null && !prop.Setter.IsAbstract))
					continue;
				prop.Generate (this, sw, indent, opt, false, true);
			}
		}
		
		bool IsExplicitlyImplementedMethod (string sig)
		{
			for (ClassGen c = this; c != null; c = c.BaseGen)
				if (c.explicitly_implemented_iface_methods.Contains (sig))
					return true;
			return false;
		}

		void GenerateInvoker (StreamWriter sw, IEnumerable<Method> methods, string indent, CodeGenerationOptions opt, HashSet<string> members, InterfaceGen gen)
		{
			foreach (Method m in methods) {
				string sig = m.GetSignature ();
				if (members.Contains (sig))
					continue;
				members.Add (sig);
				if (!m.IsAbstract)
					continue;
				if (IsExplicitlyImplementedMethod (sig)) {
					// sw.WriteLine ("// This invoker explicitly implements this method");
					m.GenerateExplicitInterfaceInvoker (sw, indent, opt, gen);
				} else {
					// sw.WriteLine ("// This invoker overrides {0} method", gen.FullName);
					m.IsOverride = true;
					m.Generate (sw, indent, opt, this, false);
					m.IsOverride = false;
				}
			}
		}

		public override void Generate (CodeGenerationOptions opt, GenerationInfo gen_info)
		{
			gen_info.CurrentType = FullName;

			StreamWriter sw = gen_info.Writer = gen_info.OpenStream(opt.GetFileName (FullName));

			sw.WriteLine ("using System;");
			sw.WriteLine ("using System.Collections.Generic;");
			sw.WriteLine ("using Android.Runtime;");
			if (opt.CodeGenerationTarget != CodeGenerationTarget.XamarinAndroid) {
				sw.WriteLine ("using Java.Interop;");
			}
			sw.WriteLine ();
			sw.WriteLine ("namespace {0} {{", Namespace);
			sw.WriteLine ();

			Generate (sw, "\t", opt, gen_info);

			sw.WriteLine ("}");
			sw.Close ();
			gen_info.Writer = null;
		}

		public static void GenerateTypeRegistrations (CodeGenerationOptions opt, GenerationInfo gen_info)
		{
			StreamWriter sw = gen_info.Writer = gen_info.OpenStream (opt.GetFileName ("Java.Interop.__TypeRegistrations"));

			Dictionary<string, List<KeyValuePair<string, string>>> mapping = new Dictionary<string, List<KeyValuePair<string, string>>>();
			foreach (KeyValuePair<string, string> reg in gen_info.TypeRegistrations) {
				int ls          = reg.Key.LastIndexOf ('/');
				string package  = ls >= 0 ? reg.Key.Substring (0, ls) : "";

				if (JniType.ToCliType (reg.Key) == reg.Value)
					continue;
				List<KeyValuePair<string, string>> v;
				if (!mapping.TryGetValue (package, out v))
					mapping.Add (package, v = new List<KeyValuePair<string, string>>());
				v.Add (new KeyValuePair<string, string>(reg.Key, reg.Value));
			}

			sw.WriteLine ("using System;");
			sw.WriteLine ("using System.Collections.Generic;");
			sw.WriteLine ("using Android.Runtime;");
			sw.WriteLine ();
			sw.WriteLine ("namespace Java.Interop {");
			sw.WriteLine ();
			sw.WriteLine ("\tpartial class __TypeRegistrations {");
			sw.WriteLine ();
			sw.WriteLine ("\t\tpublic static void RegisterPackages ()");
			sw.WriteLine ("\t\t{");
			sw.WriteLine ("#if MONODROID_TIMING");
			sw.WriteLine ("\t\t\tvar start = DateTime.Now;");
			sw.WriteLine ("\t\t\tAndroid.Util.Log.Info (\"MonoDroid-Timing\", \"RegisterPackages start: \" + (start - new DateTime (1970, 1, 1)).TotalMilliseconds);");
			sw.WriteLine ("#endif // def MONODROID_TIMING");
			sw.WriteLine ("\t\t\tJava.Interop.TypeManager.RegisterPackages (");
			sw.WriteLine ("\t\t\t\t\tnew string[]{");
			foreach (KeyValuePair<string, List<KeyValuePair<string, string>>> e in mapping) {
				sw.WriteLine ("\t\t\t\t\t\t\"{0}\",", e.Key);
			}
			sw.WriteLine ("\t\t\t\t\t},");
			sw.WriteLine ("\t\t\t\t\tnew Converter<string, Type>[]{");
			foreach (KeyValuePair<string, List<KeyValuePair<string, string>>> e in mapping) {
				sw.WriteLine ("\t\t\t\t\t\tlookup_{0}_package,", e.Key.Replace ('/', '_'));
			}
			sw.WriteLine ("\t\t\t\t\t});");
			sw.WriteLine ("#if MONODROID_TIMING");
			sw.WriteLine ("\t\t\tvar end = DateTime.Now;");
			sw.WriteLine ("\t\t\tAndroid.Util.Log.Info (\"MonoDroid-Timing\", \"RegisterPackages time: \" + (end - new DateTime (1970, 1, 1)).TotalMilliseconds + \" [elapsed: \" + (end - start).TotalMilliseconds + \" ms]\");");
			sw.WriteLine ("#endif // def MONODROID_TIMING");
			sw.WriteLine ("\t\t}");
			sw.WriteLine ();
			sw.WriteLine ("\t\tstatic Type Lookup (string[] mappings, string javaType)");
			sw.WriteLine ("\t\t{");
			sw.WriteLine ("\t\t\tstring managedType = Java.Interop.TypeManager.LookupTypeMapping (mappings, javaType);");
			sw.WriteLine ("\t\t\tif (managedType == null)");
			sw.WriteLine ("\t\t\t\treturn null;");
			sw.WriteLine ("\t\t\treturn Type.GetType (managedType);");
			sw.WriteLine ("\t\t}");
			foreach (KeyValuePair<string, List<KeyValuePair<string, string>>> map in mapping) {
				sw.WriteLine ();
				string package = map.Key.Replace ('/', '_');
				sw.WriteLine ("\t\tstatic string[] package_{0}_mappings;", package);
				sw.WriteLine ("\t\tstatic Type lookup_{0}_package (string klass)", package);
				sw.WriteLine ("\t\t{");
				sw.WriteLine ("\t\t\tif (package_{0}_mappings == null) {{", package);
				sw.WriteLine ("\t\t\t\tpackage_{0}_mappings = new string[]{{", package);
				map.Value.Sort ((a, b) => a.Key.CompareTo (b.Key));
				foreach (KeyValuePair<string, string> t in map.Value) {
					sw.WriteLine ("\t\t\t\t\t\"{0}:{1}\",", t.Key, t.Value);
				}
				sw.WriteLine ("\t\t\t\t};");
				sw.WriteLine ("\t\t\t}");
				sw.WriteLine ("");
				sw.WriteLine ("\t\t\treturn Lookup (package_{0}_mappings, klass);", package);
				sw.WriteLine ("\t\t}");
			}
			sw.WriteLine ("\t}");
			sw.WriteLine ("}");
			sw.Close ();
			gen_info.Writer = null;
		}

		public static void GenerateEnumList (GenerationInfo gen_info)
		{
			StreamWriter sw = new StreamWriter (File.Create (Path.Combine (gen_info.CSharpDir, "enumlist")));
			foreach (string e in gen_info.Enums)
				sw.WriteLine (e);
			sw.Close ();
		}
		
		protected override bool GetEnumMappedMemberInfo ()
		{
			foreach (var m in Ctors)
				return true;
			return base.GetEnumMappedMemberInfo ();
		}
		
		public override void UpdateEnumsInInterfaceImplementation ()
		{
			foreach (InterfaceGen iface in GetAllDerivedInterfaces ()) {
				if (iface.HasEnumMappedMembers) {
					foreach (Method imethod in iface.Methods) {
						var method = Methods.FirstOrDefault (m => m.Name == imethod.Name && m.JniSignature == imethod.JniSignature);
						if (method != null) {
							if (imethod.IsReturnEnumified)
								method.RetVal.SetGeneratedEnumType (imethod.RetVal.FullName);
							for (int i = 0; i < imethod.Parameters.Count; i++)
								if (imethod.Parameters [i].IsEnumified)
									method.Parameters [i].SetGeneratedEnumType (imethod.Parameters [i].Type);
						}
					}
				}
			}
			base.UpdateEnumsInInterfaceImplementation ();
		}
	}
}

