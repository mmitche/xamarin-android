using System;
using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Java.Interop.Tools.TypeNameMappings;

using Android.Runtime;

namespace Java.Interop {

	static class TypeManagerMapDictionaries
	{
		static Dictionary<string, Type>? _jniToManaged;
		static Dictionary<Type, string>? _managedToJni;

		public static readonly object AccessLock = new object ();

		//
		// Access to both properties MUST be done after taking lock on accessLock!
		//
		public static Dictionary<string, Type> JniToManaged {
			get {
				if (_jniToManaged == null)
					_jniToManaged = new Dictionary<string, Type> (StringComparer.Ordinal);
				return _jniToManaged;
			}
		}

		public static Dictionary<Type, string> ManagedToJni {
			get {
				if (_managedToJni == null)
					_managedToJni = new Dictionary<Type, string> ();
				return _managedToJni;
			}
		}
	}

	public static partial class TypeManager {
		[DllImport (AndroidRuntime.InternalDllName, CallingConvention = CallingConvention.Cdecl)]
		extern static IntPtr monodroid_TypeManager_get_java_class_name (IntPtr klass);

		internal static string GetClassName (IntPtr class_ptr)
		{
			IntPtr ptr = monodroid_TypeManager_get_java_class_name (class_ptr);
			string ret = Marshal.PtrToStringAnsi (ptr)!;
			JNIEnv.monodroid_free (ptr);

			return ret;
		}

		internal static string? GetJniTypeName (Type type)
		{
			lock (TypeManagerMapDictionaries.AccessLock) {
				if (TypeManagerMapDictionaries.ManagedToJni.TryGetValue (type, out var jni))
					return jni;
			}
			return null;
		}

		class TypeNameComparer : IComparer<string> {
			public int Compare (string x, string y)
			{
				if (object.ReferenceEquals (x, y))
					return 0;
				if (x == null)
					return -1;
				if (y == null)
					return 1;

				int xe = x.IndexOf (':');
				int ye = y.IndexOf (':');

				int r  = string.CompareOrdinal (x, 0, y, 0, System.Math.Max (xe, ye));
				if (r != 0)
					return r;

				if (xe >= 0 && ye >= 0)
					return xe - ye;

				if (xe < 0)
					return x.Length - ye;

				return xe - y.Length;
			}
		}

		static readonly TypeNameComparer JavaNameComparer = new TypeNameComparer ();

		public static string? LookupTypeMapping (string[] mappings, string javaType)
		{
			int i = Array.BinarySearch (mappings, javaType, JavaNameComparer);
			if (i < 0)
				return null;
			int c = mappings [i].IndexOf (':');
			return mappings [i].Substring (c+1);
		}

		static _JniMarshal_PPLLLL_V? cb_activate;
		internal static Delegate GetActivateHandler ()
		{
			if (cb_activate == null)
				cb_activate = (_JniMarshal_PPLLLL_V) JNINativeWrapper.CreateDelegate ((_JniMarshal_PPLLLL_V) n_Activate);
			return cb_activate;
		}

#if JAVA_INTEROP
		internal static bool ActivationEnabled {
			get { return !JniEnvironment.WithinNewObjectScope; }
		}
#else   // !JAVA_INTEROP
		[ThreadStatic]
		static bool activation_disabled;

		internal static bool ActivationEnabled {
			get { return !activation_disabled; }
			set { activation_disabled = !value; }
		}
#endif	// !JAVA_INTEROP

		static Type[] GetParameterTypes (string? signature)
		{
			if (String.IsNullOrEmpty (signature))
				return new Type[0];
			string[] typenames = signature!.Split (':');
			Type[] result = new Type [typenames.Length];
			for (int i = 0; i < typenames.Length; i++)
				result [i] = Type.GetType (typenames [i], throwOnError:true)!;
			return result;
		}

		static void n_Activate (IntPtr jnienv, IntPtr jclass, IntPtr typename_ptr, IntPtr signature_ptr, IntPtr jobject, IntPtr parameters_ptr)
		{
			var o   = Java.Lang.Object.PeekObject (jobject);
			var ex  = o as IJavaObjectEx;
			if (ex != null) {
				if (!ex.NeedsActivation && !ex.IsProxy)
					return;
			}
			if (!ActivationEnabled) {
				if (Logger.LogGlobalRef) {
					Logger.Log (LogLevel.Info, "monodroid-gref",
							string.Format ("warning: Skipping managed constructor invocation for handle 0x{0} (key_handle 0x{1}). " +
								"Please use JNIEnv.StartCreateInstance() + JNIEnv.FinishCreateInstance() instead of " +
								"JNIEnv.NewObject() and/or JNIEnv.CreateInstance().",
								jobject.ToString ("x"), JNIEnv.IdentityHash! (jobject).ToString ("x")));
				}
				return;
			}

			Type type = Type.GetType (JNIEnv.GetString (typename_ptr, JniHandleOwnership.DoNotTransfer)!, throwOnError:true)!;
			if (type.IsGenericTypeDefinition) {
				throw new NotSupportedException (
						"Constructing instances of generic types from Java is not supported, as the type parameters cannot be determined.",
						CreateJavaLocationException ());
			}
			Type[] ptypes = GetParameterTypes (JNIEnv.GetString (signature_ptr, JniHandleOwnership.DoNotTransfer));
			var parms = JNIEnv.GetObjectArray (parameters_ptr, ptypes);
			var cinfo = type.GetConstructor (ptypes);
			if (cinfo == null) {
				throw CreateMissingConstructorException (type, ptypes);
			}
			if (o != null) {
				cinfo.Invoke (o, parms);
				return;
			}

			Activate (o, jobject, cinfo, parms);
		}

		internal static void Activate (IJavaPeerable? o, IntPtr jobject, ConstructorInfo cinfo, object? []? parms)
		{
			try {
				var activator = ConstructorBuilder.CreateDelegate (cinfo);
				activator (jobject, parms);
			} catch (Exception e) {
				var m = string.Format ("Could not activate JNI Handle 0x{0} (key_handle 0x{1}) of Java type '{2}' as managed type '{3}'.",
						jobject.ToString ("x"), JNIEnv.IdentityHash! (jobject).ToString ("x"), JNIEnv.GetClassNameFromInstance (jobject), cinfo.DeclaringType.FullName);
				Logger.Log (LogLevel.Warn, "monodroid", m);
				Logger.Log (LogLevel.Warn, "monodroid", CreateJavaLocationException ().ToString ());

				throw new NotSupportedException (m, e);
			}
		}

		static Exception CreateMissingConstructorException (Type type, Type[] ptypes)
		{
			var message = new System.Text.StringBuilder ();
			message.Append ("Unable to find ");
			if (ptypes.Length == 0)
				message.Append ("the default constructor");
			else {
				message.Append ("a constructor with signature (")
					.Append (ptypes [0].FullName);
				for (int i = 1; i < ptypes.Length; ++i)
					message.Append (", ").Append (ptypes [i].FullName);
				message.Append (")");
			}
			message.Append (" on type ").Append (type.FullName)
				.Append (".  Please provide the missing constructor.");
			return new NotSupportedException (message.ToString (), CreateJavaLocationException ());
		}

		static Exception CreateJavaLocationException ()
		{
			using (var loc = new Java.Lang.Error ("Java callstack:"))
				return new JavaLocationException (loc.ToString ());
		}

		[MethodImplAttribute(MethodImplOptions.InternalCall)]
		static extern Type monodroid_typemap_java_to_managed (string java_type_name);

		internal static Type? GetJavaToManagedType (string class_name)
		{
			Type? type = monodroid_typemap_java_to_managed (class_name);
			if (type != null)
				return type;

			if (!JNIEnv.IsRunningOnDesktop) {
				// Miss message is logged in the native runtime
				if (JNIEnv.LogAssemblyCategory)
					JNIEnv.LogTypemapTrace (new System.Diagnostics.StackTrace (true));
				return null;
			}

			if (AndroidEnvironment.VSAndroidDesignerIsEnabled)
				return TypeRegistrationFallback (class_name);

			return null;
		}

		internal static Type? TypeRegistrationFallback (string class_name)
		{
			__TypeRegistrations.RegisterPackages ();

			Type? type = null;
			int ls = class_name.LastIndexOf ('/');
			var package = ls >= 0 ? class_name.Substring (0, ls) : "";
			if (packageLookup.TryGetValue (package, out var mappers)) {
				foreach (Converter<string, Type?> c in mappers) {
					type = c (class_name);
					if (type == null)
						continue;
					return type;
				}
			}
			if ((type = Type.GetType (JavaNativeTypeManager.ToCliType (class_name))) != null) {
				return type;
			}
			return null;
		}

		internal static IJavaPeerable CreateInstance (IntPtr handle, JniHandleOwnership transfer)
		{
			return CreateInstance (handle, transfer, null);
		}

		internal static IJavaPeerable CreateInstance (IntPtr handle, JniHandleOwnership transfer, Type? targetType)
		{
			Type? type = null;
			IntPtr class_ptr = JNIEnv.GetObjectClass (handle);
			string class_name = GetClassName (class_ptr);
			lock (TypeManagerMapDictionaries.AccessLock) {
				while (class_ptr != IntPtr.Zero && !TypeManagerMapDictionaries.JniToManaged.TryGetValue (class_name, out type)) {

					type = GetJavaToManagedType (class_name);
					if (type != null) {
						TypeManagerMapDictionaries.JniToManaged.Add (class_name, type);
						break;
					}

					IntPtr super_class_ptr = JNIEnv.GetSuperclass (class_ptr);
					JNIEnv.DeleteLocalRef (class_ptr);
					class_ptr = super_class_ptr;
					class_name = GetClassName (class_ptr);
				}
			}

			JNIEnv.DeleteLocalRef (class_ptr);

			if (type == null) {
				JNIEnv.DeleteRef (handle, transfer);
				throw new NotSupportedException (
						string.Format ("Internal error finding wrapper class for '{0}'. (Where is the Java.Lang.Object wrapper?!)",
							JNIEnv.GetClassNameFromInstance (handle)),
						CreateJavaLocationException ());
			}

			if (targetType != null && !targetType.IsAssignableFrom (type))
				type = targetType;

			if (type.IsInterface || type.IsAbstract) {
				var invokerType = JavaObjectExtensions.GetInvokerType (type);
				if (invokerType == null)
					throw new NotSupportedException ("Unable to find Invoker for type '" + type.FullName + "'. Was it linked away?",
							CreateJavaLocationException ());
				type = invokerType;
			}


			IJavaPeerable? result = null;

			try {
				result = (IJavaPeerable) CreateProxy (type, handle, transfer);
				if (Runtime.IsGCUserPeer (result.PeerReference.Handle)) {
					result.SetJniManagedPeerState (JniManagedPeerStates.Replaceable);
				}
			} catch (MissingMethodException e) {
				var key_handle  = JNIEnv.IdentityHash! (handle);
				JNIEnv.DeleteRef (handle, transfer);
				throw new NotSupportedException (
						string.Format ("Unable to activate instance of type {0} from native handle 0x{1} (key_handle 0x{2}).",
							type, handle.ToString ("x"), key_handle.ToString ("x")),
						e);
			}
			return result;
 		}

		static  readonly    Type[]  XAConstructorSignature  = new Type [] { typeof (IntPtr), typeof (JniHandleOwnership) };
		static  readonly    Type[]  JIConstructorSignature  = new Type [] { typeof (JniObjectReference).MakeByRefType (), typeof (JniObjectReferenceOptions) };

		internal static object CreateProxy (Type type, IntPtr handle, JniHandleOwnership transfer)
		{
			// Skip Activator.CreateInstance() as that requires public constructors,
			// and we want to hide some constructors for sanity reasons.
			BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
			var c = type.GetConstructor (flags, null, XAConstructorSignature, null);
			if (c != null) {
				return c.Invoke (new object [] { handle, transfer });
			}
			c = type.GetConstructor (flags, null, JIConstructorSignature, null);
			if (c != null) {
				JniObjectReference          r = new JniObjectReference (handle);
				JniObjectReferenceOptions   o = JniObjectReferenceOptions.Copy;
				var peer = (IJavaPeerable) c.Invoke (new object [] { r, o });
				JNIEnv.DeleteRef (handle, transfer);
				peer.SetJniManagedPeerState (peer.JniManagedPeerState | JniManagedPeerStates.Replaceable);
				return peer;
			}
			throw new MissingMethodException (
					"No constructor found for " + type.FullName + "::.ctor(System.IntPtr, Android.Runtime.JniHandleOwnership)",
					CreateJavaLocationException ());
		}

		public static void RegisterType (string java_class, Type t)
		{
			string jniFromType = JNIEnv.GetJniName (t);
			lock (TypeManagerMapDictionaries.AccessLock) {
				if (!TypeManagerMapDictionaries.JniToManaged.TryGetValue (java_class, out var lookup)) {
					TypeManagerMapDictionaries.JniToManaged.Add (java_class, t);
					if (String.Compare (jniFromType, java_class, StringComparison.OrdinalIgnoreCase) != 0) {
						TypeManagerMapDictionaries.ManagedToJni.Add (t, java_class);
					}
				} else if (!JNIEnv.IsRunningOnDesktop || t != typeof (Java.Interop.TypeManager)) {
					// skip the registration and output a warning
					Logger.Log (LogLevel.Warn, "monodroid", string.Format ("Type Registration Skipped for {0} to {1} ", java_class, t.ToString()));
				}

			}
		}

		static Dictionary<string, List<Converter<string, Type?>>>? packageLookup;

		static void LazyInitPackageLookup ()
		{
			if (packageLookup == null)
				packageLookup = new Dictionary<string, List<Converter<string, Type?>>> (StringComparer.Ordinal);
		}

		public static void RegisterPackage (string package, Converter<string, Type> lookup)
		{
			LazyInitPackageLookup ();

			lock (packageLookup) {
				if (!packageLookup.TryGetValue (package, out var lookups))
					packageLookup.Add (package, lookups = new List<Converter<string, Type?>> ());
				lookups.Add (lookup);
			}
		}

		public static void RegisterPackages (string[] packages, Converter<string, Type?>[] lookups)
		{
			LazyInitPackageLookup ();

			if (packages == null)
				throw new ArgumentNullException ("packages");
			if (lookups == null)
				throw new ArgumentNullException ("lookups");
			if (packages.Length != lookups.Length)
				throw new ArgumentException ("`packages` and `lookups` arrays must have same number of elements.");

			lock (packageLookup) {
				for (int i = 0; i < packages.Length; ++i) {
					string package                  = packages [i];
					var lookup			= lookups [i];

					if (!packageLookup.TryGetValue (package, out var _lookups))
						packageLookup.Add (package, _lookups = new List<Converter<string, Type?>> ());
					_lookups.Add (lookup);
				}
			}
		}

		[Register ("mono/android/TypeManager", DoNotGenerateAcw = true)]
		internal class JavaTypeManager : Java.Lang.Object
		{
			[Register ("activate", "(Ljava/lang/String;Ljava/lang/String;Ljava/lang/Object;[Ljava/lang/Object;)V", "")]
			static void n_Activate (IntPtr jnienv, IntPtr jclass, IntPtr typename_ptr, IntPtr signature_ptr, IntPtr jobject, IntPtr parameters_ptr)
			{
				TypeManager.n_Activate (jnienv, jclass, typename_ptr, signature_ptr, jobject, parameters_ptr);
			}

			internal static Delegate GetActivateHandler ()
			{
				return TypeManager.GetActivateHandler ();
			}
		}
	}
}
