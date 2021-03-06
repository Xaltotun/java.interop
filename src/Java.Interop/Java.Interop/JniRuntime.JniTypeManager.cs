﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;

namespace Java.Interop {

	public partial class JniRuntime {

		public class JniTypeManager : ISetRuntime {

			public      JniRuntime  Runtime { get; private set; }

			public virtual void OnSetRuntime (JniRuntime runtime)
			{
				Runtime = runtime;
			}

			public JniTypeSignature GetTypeSignature (Type type)
			{
				return GetTypeSignatures (type).FirstOrDefault ();
			}

			public IEnumerable<JniTypeSignature> GetTypeSignatures (Type type)
			{
				if (type == null)
					throw new ArgumentNullException ("type");
				if (type.GetTypeInfo ().ContainsGenericParameters)
					throw new ArgumentException ("Generic type definitions are not supported.", "type");

				return CreateGetTypeSignaturesEnumerator (type);
			}

			IEnumerable<JniTypeSignature> CreateGetTypeSignaturesEnumerator (Type type)
			{
				var originalType    = type;
				int rank            = 0;
				while (type.IsArray) {
					if (type.IsArray && type.GetArrayRank () > 1)
						throw new ArgumentException ("Multidimensional array '" + originalType.FullName + "' is not supported.", "type");
					rank++;
					type    = type.GetElementType ();
				}

				var info    = type.GetTypeInfo ();
				if (info.IsEnum)
					type = Enum.GetUnderlyingType (type);

#if !XA_INTEGRATION
				foreach (var mapping in JniBuiltinTypeNameMappings) {
					if (mapping.Key == type) {
						var r = mapping.Value;
						yield return r.AddArrayRank (rank);
					}
				}

				foreach (var mapping in JniBuiltinArrayMappings) {
					if (mapping.Key == type) {
						var r = mapping.Value;
						yield return r.AddArrayRank (rank);
					}
				}
#endif  // !XA_INTEGRATION

				var name = info.GetCustomAttribute<JniTypeSignatureAttribute> (inherit: false);
				if (name != null) {
					yield return new JniTypeSignature (name.SimpleReference, name.ArrayRank + rank, name.IsKeyword);
				}

				var isGeneric   = info.IsGenericType;
				var genericDef  = isGeneric ? info.GetGenericTypeDefinition () : type;
#if !XA_INTEGRATION
				if (isGeneric) {
					if (genericDef == typeof(JavaArray<>) || genericDef == typeof(JavaObjectArray<>)) {
						var r = GetTypeSignature (info.GenericTypeArguments [0]);
						yield return r.AddArrayRank (rank + 1);
					}
				}
#endif  // !XA_INTEGRATION
				foreach (var simpleRef in GetSimpleReferences (type)) {
					if (simpleRef == null)
						continue;
					yield return new JniTypeSignature (simpleRef, rank, false);
				}

				if (isGeneric) {
					foreach (var simpleRef in GetSimpleReferences (genericDef)) {
						if (simpleRef == null)
							continue;
						yield return new JniTypeSignature (simpleRef, rank, false);
					}
				}
			}

			// `type` will NOT be an array type.
			protected virtual IEnumerable<string> GetSimpleReferences (Type type)
			{
				if (type == null)
					throw new ArgumentNullException ("type");
				if (type.IsArray)
					throw new ArgumentException ("Array type '" + type.FullName + "' is not supported.", "type");
				return EmptyStringArray;
			}

			static  readonly    string[]    EmptyStringArray    = new string [0];
			static  readonly    Type[]      EmptyTypeArray      = new Type [0];


			public  Type    GetType (JniTypeSignature typeSignature)
			{
				return GetTypes (typeSignature).FirstOrDefault ();
			}

			public virtual IEnumerable<Type> GetTypes (JniTypeSignature typeSignature)
			{
				if (typeSignature.SimpleReference == null)
					return EmptyTypeArray;
				return CreateGetTypesEnumerator (typeSignature);
			}

			IEnumerable<Type> CreateGetTypesEnumerator (JniTypeSignature typeSignature)
			{
				foreach (var type in GetTypesForSimpleReference (typeSignature.SimpleReference)) {
					if (typeSignature.ArrayRank == 0) {
						yield return type;
						continue;
					}
#if !XA_INTEGRATION
					if (typeSignature.ArrayRank > 0) {
						var rank        = typeSignature.ArrayRank;
						var arrayType   = type;
						if (typeSignature.IsKeyword) {
							arrayType   = typeof (JavaPrimitiveArray<>).MakeGenericType (arrayType);
							rank--;
						}
						while (rank-- > 0) {
							arrayType   = typeof (JavaObjectArray<>).MakeGenericType (arrayType);
						}
						yield return arrayType;
					}
#endif  // !XA_INTEGRATION
					if (typeSignature.ArrayRank > 0) {
						var rank        = typeSignature.ArrayRank;
						var arrayType   = type;
						while (rank-- > 0) {
							arrayType   = arrayType.MakeArrayType ();
						}
						yield return arrayType;
					}
				}
			}

			protected virtual IEnumerable<Type> GetTypesForSimpleReference (string jniSimpleReference)
			{
				if (jniSimpleReference == null)
					throw new ArgumentNullException (nameof (jniSimpleReference));
				if (jniSimpleReference != null && jniSimpleReference.Contains ("."))
					throw new ArgumentException ("JNI type names do not contain '.', they use '/'. Are you sure you're using a JNI type name?", nameof (jniSimpleReference));
				if (jniSimpleReference != null && jniSimpleReference.StartsWith ("[", StringComparison.Ordinal))
					throw new ArgumentException ("Only simplified type references are supported.", nameof (jniSimpleReference));
				if (jniSimpleReference != null && jniSimpleReference.StartsWith ("L", StringComparison.Ordinal) && jniSimpleReference.EndsWith (";", StringComparison.Ordinal))
					throw new ArgumentException ("Only simplified type references are supported.", nameof (jniSimpleReference));

				return CreateGetTypesForSimpleReferenceEnumerator (jniSimpleReference);
			}

			IEnumerable<Type> CreateGetTypesForSimpleReferenceEnumerator (string jniSimpleReference)
			{
#if !XA_INTEGRATION
				foreach (var mapping in JniBuiltinTypeNameMappings) {
					if (mapping.Value.SimpleReference == jniSimpleReference)
						yield return mapping.Key;
				}
#endif  // !XA_INTEGRATION
				yield break;
			}

			public virtual void RegisterNativeMembers (JniType nativeClass, Type type, string methods)
			{
				if (!TryLoadExternalJniMarshalMethods (nativeClass, type, methods) &&
						!TryRegisterNativeMembers (nativeClass, type, methods)) {
					throw new NotSupportedException ($"Could not register Java.class={nativeClass.Name} Managed.type={type.FullName}");
				}
			}

			static bool TryLoadExternalJniMarshalMethods (JniType nativeClass, Type type, string methods)
			{
				var marshalMethodAssemblyName   = new AssemblyName (type.GetTypeInfo ().Assembly.GetName ().Name + "-JniMarshalMethods");
				var marshalMethodsAssembly      = TryLoadAssembly (marshalMethodAssemblyName);
				if (marshalMethodsAssembly == null)
					return false;

				var marshalType = marshalMethodsAssembly.GetType (type.FullName);
				if (marshalType == null)
					return false;
				return TryRegisterNativeMembers (nativeClass, marshalType, methods);
			}

			static Assembly TryLoadAssembly (AssemblyName name)
			{
				try {
					return Assembly.Load (name);
				}
				catch (Exception e) {
					Debug.WriteLine ("Warning: Could not load JNI Marshal Method assembly '{0}': {1}", name, e);
					return null;
				}
			}

			static bool TryRegisterNativeMembers (JniType nativeClass, Type marshalType, string methods)
			{
				var registerMethod  = marshalType.GetTypeInfo ().GetDeclaredMethod ("__RegisterNativeMembers");
				if (registerMethod == null) {
					return false;
				}

				var register    = (Action<JniType, string>) registerMethod.CreateDelegate (typeof(Action<JniType, string>));
				register (nativeClass, methods);
				return true;
			}
		}
	}
}

