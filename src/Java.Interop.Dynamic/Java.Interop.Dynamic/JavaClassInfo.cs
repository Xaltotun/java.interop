﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Dynamic;
using System.Runtime.CompilerServices;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Text;

using Mono.Linq.Expressions;

using Java.Interop;

namespace Java.Interop.Dynamic {

	class JavaClassInfo : IDisposable {

		static  readonly    Func<string, Type, JniPeerMembers>  CreatePeerMembers;

		static  readonly    JniInstanceMethodID                 Class_getConstructors;
		static  readonly    JniInstanceMethodID                 Class_getFields;
		static  readonly    JniInstanceMethodID                 Class_getMethods;

		static  readonly    JniInstanceMethodID                 Constructor_getParameterTypes;

		static  readonly    JniInstanceMethodID                 Field_getName;
		static  readonly    JniInstanceMethodID                 Field_getType;

		static  readonly    JniInstanceMethodID                 Method_getName;
		static  readonly    JniInstanceMethodID                 Method_getReturnType;
		static  readonly    JniInstanceMethodID                 Method_getParameterTypes;

		static  readonly    JniInstanceMethodID                 Member_getModifiers;

		static  readonly    Dictionary<string, WeakReference>   Classes = new Dictionary<string, WeakReference> ();

		static JavaClassInfo ()
		{
			CreatePeerMembers = (Func<string, Type, JniPeerMembers>)
				Delegate.CreateDelegate (
					typeof(Func<string, Type, JniPeerMembers>),
					typeof(JniPeerMembers).GetMethod ("CreatePeerMembers", BindingFlags.NonPublic | BindingFlags.Static));
			if (CreatePeerMembers == null)
				throw new NotSupportedException ("Could not find JniPeerMembers.CreatePeerMembers!");

			using (var t = new JniType ("java/lang/Class")) {
				Class_getConstructors   = t.GetInstanceMethod ("getConstructors", "()[Ljava/lang/reflect/Constructor;");
				Class_getFields         = t.GetInstanceMethod ("getFields", "()[Ljava/lang/reflect/Field;");
				Class_getMethods        = t.GetInstanceMethod ("getMethods", "()[Ljava/lang/reflect/Method;");
			}
			using (var t = new JniType ("java/lang/reflect/Constructor")) {
				Constructor_getParameterTypes   = t.GetInstanceMethod ("getParameterTypes", "()[Ljava/lang/Class;");
			}
			using (var t = new JniType ("java/lang/reflect/Field")) {
				Field_getName   = t.GetInstanceMethod ("getName", "()Ljava/lang/String;");
				Field_getType   = t.GetInstanceMethod ("getType", "()Ljava/lang/Class;");
			}
			using (var t = new JniType ("java/lang/reflect/Method")) {
				Method_getName              = t.GetInstanceMethod ("getName", "()Ljava/lang/String;");
				Method_getParameterTypes    = t.GetInstanceMethod ("getParameterTypes", "()[Ljava/lang/Class;");
				Method_getReturnType        = t.GetInstanceMethod ("getReturnType", "()Ljava/lang/Class;");
			}
			using (var t = new JniType ("java/lang/reflect/Member")) {
				Member_getModifiers     = t.GetInstanceMethod ("getModifiers", "()I");
			}
		}

		public  static  JavaClassInfo   GetClassInfo (string jniClassName)
		{
			lock (Classes) {
				JavaClassInfo info = _GetClassInfo (jniClassName);
				if (info != null) {
					Interlocked.Increment (ref info.RefCount);
					return info;
				}

				info    = new JavaClassInfo (jniClassName);
				Classes.Add (jniClassName, new WeakReference (info));
				return info;
			}
		}

		static JavaClassInfo _GetClassInfo (string jniClassName)
		{
			lock (Classes) {
				WeakReference value;
				if (Classes.TryGetValue (jniClassName, out value)) {
					var info    = (JavaClassInfo) value.Target;
					if (info != null) {
						return info;
					}
					Classes.Remove (jniClassName);
				}

				return null;
			}
		}

		// For testing
		public  static  int GetClassInfoCount (string jniClassName)
		{
			lock (Classes) {
				var info    = _GetClassInfo (jniClassName);
				if (info != null)
					return info.RefCount;
				return -1;
			}
		}

		public      string              JniClassName        {get; private set;}

		internal    bool                Disposed;
		internal    JniPeerMembers      Members;

		int         RefCount    = 1;

		List<JavaConstructorInfo>                       constructors;
		Dictionary<string, List<JavaFieldInfo>>         fields;
		Dictionary<string, List<JavaMethodInfo>>        methods;

		public  List<JavaConstructorInfo>               Constructors {
			get {return LookupConstructors ();}
		}

		public  Dictionary<string, List<JavaFieldInfo>> Fields {
			get {return LookupFields ();}
		}

		public Dictionary<string, List<JavaMethodInfo>> Methods {
			get {return LookupMethods ();}
		}


		JavaClassInfo (string jniClassName)
		{
			if (jniClassName == null)
				throw new ArgumentNullException ("jniClassName");

			JniClassName    = jniClassName;
			Members         = CreatePeerMembers (jniClassName, typeof (JavaInstanceProxy));
		}

		public void Dispose ()
		{
			lock (Classes) {
				if (Interlocked.Decrement (ref RefCount) != 0)
					return;

				Disposed = true;

				if (methods != null) {
					foreach (var name in methods.Keys.ToList ()) {
						foreach (var info in methods [name])
							info.Dispose ();
						methods [name]    = null;
					}
				}

				JniPeerMembers.Dispose (Members);
				Classes.Remove (JniClassName);
			}
		}

		internal static JniReferenceSafeHandle GetMethodParameters (JniReferenceSafeHandle method)
		{
			return Method_getParameterTypes.CallVirtualObjectMethod (method);
		}

		internal static JniReferenceSafeHandle GetConstructorParameters (JniReferenceSafeHandle method)
		{
			return Constructor_getParameterTypes.CallVirtualObjectMethod (method);
		}

		List<JavaConstructorInfo> LookupConstructors ()
		{
			if (Members == null)
				return null;

			lock (Members) {
				if (constructors != null || Disposed)
					return constructors;

				constructors    = new List<JavaConstructorInfo> ();

				using (var ctors = Class_getConstructors.CallVirtualObjectMethod (Members.JniPeerType.SafeHandle)) {
					int len     = JniEnvironment.Arrays.GetArrayLength (ctors);
					for (int i  = 0; i < len; ++i) {
						var ctor    = JniEnvironment.Arrays.GetObjectArrayElement (ctors, i);
						var m       = new JavaConstructorInfo (Members, ctor);
						constructors.Add (m);
						ctor.Dispose ();
					}
				}

				return constructors;
			}
		}

		Dictionary<string, List<JavaFieldInfo>> LookupFields ()
		{
			if (Members == null)
				return null;

			lock (Members) {
				if (fields != null || Disposed)
					return this.fields;

				this.fields = new Dictionary<string, List<JavaFieldInfo>> ();

				using (var fields = Class_getFields.CallVirtualObjectMethod (Members.JniPeerType.SafeHandle)) {
					int len     = JniEnvironment.Arrays.GetArrayLength (fields);
					for (int i  = 0; i < len; ++i) {
						var field       = JniEnvironment.Arrays.GetObjectArrayElement (fields, i);
						var name        = JniEnvironment.Strings.ToString (Field_getName.CallVirtualObjectMethod (field), JniHandleOwnership.Transfer);
						var isStatic    = IsStatic (field);

						List<JavaFieldInfo> overloads;
						if (!Fields.TryGetValue (name, out overloads))
							Fields.Add (name, overloads = new List<JavaFieldInfo> ());

						using (var type = new JniType (Field_getType.CallVirtualObjectMethod (field), JniHandleOwnership.Transfer)) {
							var info = JniEnvironment.Current.JavaVM.GetJniTypeInfoForJniTypeReference (type.Name);
							overloads.Add (new JavaFieldInfo (Members, name + "\u0000" + info.JniTypeReference, isStatic));
						}

						field.Dispose ();
					}
				}

				return this.fields;
			}
		}

		Dictionary<string, List<JavaMethodInfo>> LookupMethods ()
		{
			if (Members == null)
				return null;

			lock (Members) {
				if (methods != null || Disposed)
					return this.methods;

				this.methods = new Dictionary<string, List<JavaMethodInfo>> ();

				using (var methods = Class_getMethods.CallVirtualObjectMethod (Members.JniPeerType.SafeHandle)) {
					int len     = JniEnvironment.Arrays.GetArrayLength (methods);
					for (int i  = 0; i < len; ++i) {
						var method      = JniEnvironment.Arrays.GetObjectArrayElement (methods, i);
						var name        = JniEnvironment.Strings.ToString (Method_getName.CallVirtualObjectMethod (method), JniHandleOwnership.Transfer);
						var isStatic    = IsStatic (method);

						List<JavaMethodInfo> overloads;
						if (!Methods.TryGetValue (name, out overloads))
							Methods.Add (name, overloads = new List<JavaMethodInfo> ());

						var rt  = new JniType (Method_getReturnType.CallVirtualObjectMethod (method), JniHandleOwnership.Transfer);
						var m   = new JavaMethodInfo (Members, method, name, isStatic) {
							ReturnType  = rt,
						};
						overloads.Add (m);
						method.Dispose ();
					}
				}

				return this.methods;
			}
		}

		static bool IsStatic (JniReferenceSafeHandle member)
		{
			var s   = Member_getModifiers.CallVirtualInt32Method (member);

			return (s & JavaModifiers.Static) == JavaModifiers.Static;
		}

		internal unsafe bool TryInvokeMember (IJavaObject self, JavaMethodBase[] overloads, DynamicMetaObject[] args, out object value)
		{
			value       = null;
			var margs   = (List<JniArgumentMarshalInfo>) null;

			var jtypes  = GetJniTypes (args);
			try {
				var matches = overloads.Where (o => (o.IsConstructor || o.IsStatic == (self == null)) && o.CompatibleWith (jtypes, args));
				var invoke  = matches.FirstOrDefault ();
				if (invoke == null)
					return false;

				margs       = args.Select (arg => new JniArgumentMarshalInfo (arg.Value, arg.LimitType)).ToList ();
				var jargs   = stackalloc JValue [margs.Count];
				for (int i = 0; i < margs.Count; ++i)
					jargs [i] = margs [i].JValue;
				value       = invoke.Invoke (self, jargs);
				return true;
			}
			finally {
				for (int i = 0; margs != null && i < margs.Count; ++i) {
					margs [i].Cleanup (args [i]);
				}
				for (int i = 0; i < jtypes.Count; ++i) {
					if (jtypes [i] != null)
						jtypes [i].Dispose ();
				}
			}
		}

		static List<JniType> GetJniTypes (DynamicMetaObject[] args)
		{
			var r   = new List<JniType> (args.Length);
			var vm  = JniEnvironment.Current.JavaVM;
			foreach (var a in args) {
				try {
					var at  = new JniType (vm.GetJniTypeInfoForType (a.LimitType).JniTypeReference);
					r.Add (at);
				} catch (JavaException e) {
					e.Dispose ();
					r.Add (null);
				} catch {
					r.Add (null);
				}
			}
			return r;
		}
	}
}
