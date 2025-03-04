using System;
using System.Linq;
using System.IO;
using System.Diagnostics;
using Microsoft.Build.Framework;
using System.Text;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.XPath;
using System.Xml.Linq;

namespace Xamarin.ProjectTools
{
	public class Builder : IDisposable
	{
		const string SigSegvError = "Got a SIGSEGV while executing native code";
		const string ConsoleLoggerError = "[ERROR] FATAL UNHANDLED EXCEPTION: System.ArgumentException: is negative";

		string Arm32AbiDir => UseDotNet ? "armeabi-v7a-net6" : "armeabi-v7a";

		/// <summary>
		/// If true, use `dotnet build` and IShortFormProject throughout the tests
		/// </summary>
		public static bool UseDotNet => Environment.Version.Major >= 5;

		string root;
		string buildLogFullPath;
		public bool IsUnix { get; set; }
		/// <summary>
		/// This passes /p:BuildingInsideVisualStudio=True, command-line to MSBuild
		/// </summary>
		public bool BuildingInsideVisualStudio { get; set; } = true;
		/// <summary>
		/// Passes /m:N to MSBuild, defaults to null to omit the /m parameter completely.
		/// </summary>
		public int? MaxCpuCount { get; set; }
		public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Diagnostic;
		public IEnumerable<string> LastBuildOutput {
			get {
				if (!string.IsNullOrEmpty (buildLogFullPath) && File.Exists (buildLogFullPath)) {
					return File.ReadLines (buildLogFullPath, Encoding.UTF8);
				}
				return Enumerable.Empty<string> ();
			}
		}
		public TimeSpan LastBuildTime { get; protected set; }
		public string BuildLogFile { get; set; }
		public bool ThrowOnBuildFailure { get; set; }
		/// <summary>
		/// True if NuGet restore occurs automatically (default)
		/// </summary>
		public bool AutomaticNuGetRestore { get; set; } = true;

		public string BuildTool {
			get {
				if (UseDotNet)
					return Path.Combine (AndroidSdkResolver.GetDotNetPreviewPath (), "dotnet");

				string xabuild;
				if (IsUnix) {
					xabuild = XABuildPaths.XABuildScript;
					if (File.Exists (xabuild))
						return xabuild;
					xabuild = Path.GetFullPath (Path.Combine (Root, "..", "..", "..", "..", "..", "..", "..", "out", "bin", "xabuild"));
					if (File.Exists (xabuild))
						return xabuild;
					return "msbuild";
				}

				xabuild = XABuildPaths.XABuildExe;
				if (File.Exists (xabuild))
					return xabuild;
				return IsUnix ? "msbuild" : TestEnvironment.GetVisualStudioInstance ().MSBuildPath;
			}
		}

		/// <summary>
		/// The top directory of a local build tree if it can be found, e.g. xamarin-android/bin/Debug.
		/// </summary>
		public string BuildOutputDirectory {
			get {
				var outdir = Environment.GetEnvironmentVariable ("XA_BUILD_OUTPUT_PATH");
				string configuration = Environment.GetEnvironmentVariable ("CONFIGURATION") ?? XABuildPaths.Configuration;
				var libmonodroidPath = Path.Combine ("lib", "xamarin.android", "xbuild", "Xamarin", "Android", "lib", Arm32AbiDir, "libmono-android.release.so");
				if (String.IsNullOrEmpty(outdir))
					outdir = Path.GetFullPath (Path.Combine (Root, "..", "..", "..", "..", "..", "..", "..", "out"));
				if (!Directory.Exists (Path.Combine (outdir, "lib")) || !File.Exists (Path.Combine (outdir, libmonodroidPath)))
					outdir = Path.Combine (XABuildPaths.TopDirectory, "bin", configuration);
				if (!Directory.Exists (Path.Combine (outdir, "lib")) || !File.Exists (Path.Combine (outdir, libmonodroidPath)))
					outdir = XABuildPaths.PrefixDirectory;
				if (!Directory.Exists (Path.Combine (outdir, "lib")) || !File.Exists (Path.Combine (outdir, libmonodroidPath)))
					outdir = Path.Combine (XABuildPaths.TopDirectory, "bin", "Debug");
				if (!Directory.Exists (Path.Combine (outdir, "lib")) || !File.Exists (Path.Combine (outdir, libmonodroidPath)))
					outdir = Path.Combine (XABuildPaths.TopDirectory, "bin", "Release");

				return outdir;
			}
		}

		/// <summary>
		/// The MonoAndroidTools directory within a local build tree, e.g. xamarin-android/bin/Debug/lib/xamarin.android/xbuild/Xamarin/Android.<br/>
		/// If a local build tree can not be found, or if it is empty, this will return the system installation or .NET sandbox location instead:<br/>
		///	Windows:  C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\MSBuild\Xamarin\Android <br/>
		///	macOS:    /Library/Frameworks/Xamarin.Android.framework/Versions/Current/lib/xamarin.android/xbuild/Xamarin/Android<br/>
		///	Windows (dotnet):  %USERPROFILE%\android-toolchain\dotnet\packs\Microsoft.Android.Sdk.Windows\$(Latest)\tools<br/>
		///	macOS (dotnet):    $HOME/android-toolchain/dotnet/packs/Microsoft.Android.Sdk.Darwin/$(Latest)/tools
		/// </summary>
		public string AndroidMSBuildDirectory {
			get {
				var msbuildDir = Path.Combine (BuildOutputDirectory, "lib", "xamarin.android", "xbuild", "Xamarin", "Android");
				if (Directory.Exists (msbuildDir) && File.Exists (Path.Combine (msbuildDir, "lib", Arm32AbiDir, "libmono-android.release.so")))
					return msbuildDir;

				if (UseDotNet) {
					return TestEnvironment.DotNetAndroidSdkToolsDirectory;
				} else {
					return TestEnvironment.MonoAndroidToolsDirectory;
				}
			}
		}

		/// <summary>
		/// The MonoAndroid framework (and other reference assemblies) directory within a local build tree. Contains v1.0, v9.0, etc,
		/// e.g. xamarin-android/bin/Debug/lib/xamarin.android/xbuild-frameworks/MonoAndroid.<br/>
		/// If a local build tree can not be found, or if it is empty, this will return the system installation location instead:<br/>
		///	Windows:  C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise\Common7\IDE\ReferenceAssemblies\Microsoft\Framework\MonoAndroid <br/>
		///	macOS:    Library/Frameworks/Xamarin.Android.framework/Versions/Current/lib/xamarin.android/xbuild-frameworks/MonoAndroid
		/// </summary>
		public string FrameworkLibDirectory {
			get {
				var frameworkLibDir = Path.Combine (BuildOutputDirectory, "lib", "xamarin.android", "xbuild-frameworks", "MonoAndroid");
				if (Directory.Exists (frameworkLibDir) && Directory.EnumerateDirectories (frameworkLibDir, "v*", SearchOption.TopDirectoryOnly).Any ())
					return frameworkLibDir;

				return TestEnvironment.MonoAndroidFrameworkDirectory;
			}
		}

		public bool CrossCompilerAvailable (string supportedAbis)
		{
			var crossCompilerLookup = new Dictionary<string, string> {
				{ "armeabi-v7a", "cross-arm" },
				{ "x86", "cross-x86" },
				{ "x86_64", "cross-x86_64" },
				{ "arm64-v8a", "cross-arm64" },
			};
			bool result = true;
			foreach (var abi in supportedAbis.Split (';')) {
				var fileName = crossCompilerLookup [abi];
				var path = AndroidMSBuildDirectory;
				if (IsUnix) {
					result &= (File.Exists (Path.Combine (path, "Darwin", fileName)) ||
						File.Exists (Path.Combine (path, "Linux", fileName)));
				} else {
					result &= File.Exists (Path.Combine (path, fileName + ".exe"));
				}
			}
			return result;
		}

		public string FirstTargetFrameworkVersion ()
		{
			GetTargetFrameworkVersionRange (out string _, out string firstFrameworkVersion, out string _, out string _, out string[] _);
			return firstFrameworkVersion;
		}

		public string FirstTargetFrameworkVersion (out string apiLevel)
		{
			GetTargetFrameworkVersionRange (out apiLevel, out string firstFrameworkVersion, out string _, out string _, out string [] _);
			return firstFrameworkVersion;
		}

		public string LatestTargetFrameworkVersion () {
			GetTargetFrameworkVersionRange (out string _, out string _, out string _, out string lastFrameworkVersion, out string [] _);
			return lastFrameworkVersion;
		}

		public string LatestTargetFrameworkVersion (out string apiLevel) {
			GetTargetFrameworkVersionRange (out string _, out string _, out apiLevel, out string lastFrameworkVersion, out string [] _);
			return lastFrameworkVersion;
		}

		public string[] GetAllSupportedTargetFrameworkVersions ()
		{
			GetTargetFrameworkVersionRange (out string _, out string _, out string _, out string _, out string [] allFrameworkVersions);
			return allFrameworkVersions;
		}

		public void GetTargetFrameworkVersionRange (out string firstApiLevel, out string firstFrameworkVersion, out string lastApiLevel, out string lastFrameworkVersion, out string[] allFrameworkVersions)
		{
			firstApiLevel = firstFrameworkVersion = lastApiLevel = lastFrameworkVersion = null;

			Version firstVersion    = null;
			Version lastVersion     = null;
			List<string> allTFVs    = new List<string> ();

			var searchDir = UseDotNet ? Path.Combine (TestEnvironment.DotNetAndroidSdkDirectory, "data") : FrameworkLibDirectory;
			foreach (var apiInfoFile in Directory.EnumerateFiles (searchDir, "AndroidApiInfo.xml", SearchOption.AllDirectories)) {
				string frameworkVersion = GetApiInfoElementValue (apiInfoFile, "/AndroidApiInfo/Version");
				string apiLevel         = GetApiInfoElementValue (apiInfoFile, "/AndroidApiInfo/Level");
				bool.TryParse (GetApiInfoElementValue (apiInfoFile, "/AndroidApiInfo/Stable"), out bool isStable);
				if (!isStable || !Version.TryParse (frameworkVersion.Replace ("v", ""), out Version version))
					continue;
				if (firstVersion == null || version < firstVersion) {
					firstVersion            = version;
					firstFrameworkVersion   = frameworkVersion;
					firstApiLevel           = apiLevel;
				}
				if (lastVersion == null || version > lastVersion) {
					lastVersion             = version;
					lastFrameworkVersion    = frameworkVersion;
					lastApiLevel            = apiLevel;
				}
				allTFVs.Add (frameworkVersion);
			}
			allFrameworkVersions = allTFVs.ToArray ();
		}

		static string GetApiInfoElementValue (string androidApiInfo, string elementPath)
		{
			if (!File.Exists (androidApiInfo))
				return null;

			var doc = XDocument.Load (androidApiInfo);
			return doc.XPathSelectElement (elementPath)?.Value;
		}

		public bool TargetFrameworkExists (string targetFramework)
		{
			var path = Path.Combine (FrameworkLibDirectory, targetFramework);
			if (!Directory.Exists (path)) {
				return false;
			}
			return true;
		}


		public string Root {
			get {
				return String.IsNullOrEmpty (root) ? Path.GetDirectoryName (new Uri (typeof (XamarinProject).Assembly.CodeBase).LocalPath) : root;
			}
			set { root = value; }
		}

		public Builder ()
		{
			IsUnix = Environment.OSVersion.Platform != PlatformID.Win32NT;
			BuildLogFile = "build.log";
		}

		public void Dispose ()
		{
			Dispose (true);
			GC.SuppressFinalize (this);
		}

		protected virtual void Dispose (bool disposing)
		{
		}

		Regex timeElapsedRegEx = new Regex (
			@"^Time Elapsed([\s])(?<TimeSpan>(\d+):(\d\d):(\d\d)\.(\d+))$",
			RegexOptions.Multiline | RegexOptions.Compiled
		);

		protected bool BuildInternal (string projectOrSolution, string target, string [] parameters = null, Dictionary<string, string> environmentVariables = null, bool restore = true)
		{
			buildLogFullPath = (!string.IsNullOrEmpty (BuildLogFile))
				? Path.GetFullPath (Path.Combine (XABuildPaths.TestOutputDirectory, Path.GetDirectoryName (projectOrSolution), BuildLogFile))
				: null;
			string processLog = !string.IsNullOrEmpty (BuildLogFile)
				? Path.Combine (Path.GetDirectoryName (buildLogFullPath), "process.log")
				: null;

			var logger = buildLogFullPath == null
				? string.Empty
				: string.Format ("/noconsolelogger \"/flp1:LogFile={0};Encoding=UTF-8;Verbosity={1}\"",
					buildLogFullPath, Verbosity.ToString ().ToLower ());

			var start = DateTime.UtcNow;
			var args  = new StringBuilder ();
			var psi   = new ProcessStartInfo (BuildTool);
			var responseFile = Path.Combine (XABuildPaths.TestOutputDirectory, Path.GetDirectoryName (projectOrSolution), "project.rsp");
			if (UseDotNet)
				args.Append ("build ");
			args.AppendFormat ("{0} /t:{1} {2}",
					QuoteFileName (Path.Combine (XABuildPaths.TestOutputDirectory, projectOrSolution)), target, logger);
			if (UseDotNet) {
				if (!AutomaticNuGetRestore) {
					args.Append (" --no-restore");
				}
			} else if (AutomaticNuGetRestore && restore) {
				args.Append (" /restore");
			}

			args.Append (" -nodeReuse:false"); // Disable the MSBuild daemon everywhere!

			if (MaxCpuCount != null) {
				if (!string.Equals (Path.GetFileNameWithoutExtension (psi.FileName), "xabuild", StringComparison.OrdinalIgnoreCase)) {
					args.Append ($" /maxCpuCount:{MaxCpuCount}");
				} else {
					Console.WriteLine ($"Ignoring MaxCpuCount={MaxCpuCount}, running with xabuild.");
				}
			}
			args.Append ($" @\"{responseFile}\"");
			using (var sw = new StreamWriter (responseFile, append: false, encoding: Encoding.UTF8)) {
				sw.WriteLine ($" /p:BuildingInsideVisualStudio={BuildingInsideVisualStudio}");
				if (BuildingInsideVisualStudio) {
					sw.WriteLine (" /p:BuildingOutOfProcess=true");
				}
				string sdkPath = AndroidSdkResolver.GetAndroidSdkPath ();
				if (Directory.Exists (sdkPath)) {
					sw.WriteLine (" /p:AndroidSdkDirectory=\"{0}\" ", sdkPath);
				}
				string jdkPath = AndroidSdkResolver.GetJavaSdkPath ();
				if (Directory.Exists (jdkPath)) {
					sw.WriteLine (" /p:JavaSdkDirectory=\"{0}\" ", jdkPath);
				}
				if (parameters != null) {
					foreach (var param in parameters) {
						sw.WriteLine (" /p:{0}", param);
					}
				}
				var msbuildArgs = Environment.GetEnvironmentVariable ("NUNIT_MSBUILD_ARGS");
				if (!string.IsNullOrEmpty (msbuildArgs)) {
					sw.WriteLine (msbuildArgs);
				}

				psi.SetEnvironmentVariable ("MSBUILD", "msbuild");
				sw.WriteLine ($" /bl:\"{Path.GetFullPath (Path.Combine (XABuildPaths.TestOutputDirectory, Path.GetDirectoryName (projectOrSolution), "msbuild.binlog"))}\"");

				if (environmentVariables != null) {
					foreach (var kvp in environmentVariables) {
						psi.SetEnvironmentVariable (kvp.Key, kvp.Value);
					}
				}
			}

			//NOTE: commit messages can "accidentally" cause test failures
			// Consider if you added an error message in a commit message, then wrote a test asserting the error no longer occurs.
			// Both Jenkins and VSTS have an environment variable containing the full commit message, which will inexplicably cause your test to fail...
			// For a Jenkins case, see https://github.com/xamarin/xamarin-android/pull/1049#issuecomment-347625456
			// For a VSTS case, see http://build.devdiv.io/1806783
			psi.SetEnvironmentVariable ("ghprbPullLongDescription", "");
			psi.SetEnvironmentVariable ("BUILD_SOURCEVERSIONMESSAGE", "");

			// Ensure any variable alteration from DotNetXamarinProject.Construct is cleared.
			if (!Builder.UseDotNet && !TestEnvironment.IsWindows) {
				psi.SetEnvironmentVariable ("MSBUILD_EXE_PATH", null);
			}
			if (Builder.UseDotNet) {
				psi.SetEnvironmentVariable ("DOTNET_MULTILEVEL_LOOKUP", "0");
			}

			psi.Arguments = args.ToString ();

			psi.CreateNoWindow = true;
			psi.UseShellExecute = false;
			psi.RedirectStandardOutput = true;
			psi.RedirectStandardError = true;
			psi.StandardErrorEncoding = Encoding.UTF8;
			psi.StandardOutputEncoding = Encoding.UTF8;

			bool nativeCrashDetected = false;
			bool result = false;
			bool ranToCompletion = false;
			int attempts = 1;
			ManualResetEvent err = new ManualResetEvent (false);
			ManualResetEvent stdout = new ManualResetEvent (false);
			for (int attempt = 0; attempt < attempts; attempt++) {
				if (processLog != null)
					File.AppendAllText (processLog, psi.FileName + " " + args.ToString () + Environment.NewLine);
				using (var p = new Process ()) {
					p.ErrorDataReceived += (sender, e) => {
						if (e.Data != null && !string.IsNullOrEmpty (processLog)) {
							File.AppendAllText (processLog, e.Data + Environment.NewLine);
							if (e.Data.StartsWith (SigSegvError, StringComparison.OrdinalIgnoreCase)) {
								nativeCrashDetected = true;
							}
							if (e.Data.StartsWith (ConsoleLoggerError, StringComparison.OrdinalIgnoreCase)) {
								nativeCrashDetected = true;
							}
						}
						if (e.Data == null)
							err.Set ();
					};
					p.OutputDataReceived += (sender, e) => {
						if (e.Data != null && !string.IsNullOrEmpty (processLog)) {
							File.AppendAllText (processLog, e.Data + Environment.NewLine);
							if (e.Data.StartsWith (SigSegvError, StringComparison.OrdinalIgnoreCase)) {
								nativeCrashDetected = true;
							}
							if (e.Data.StartsWith (ConsoleLoggerError, StringComparison.OrdinalIgnoreCase)) {
								nativeCrashDetected = true;
							}
						}
						if (e.Data == null)
							stdout.Set ();
					};
					p.StartInfo = psi;
					Console.WriteLine ($"{psi.FileName} {psi.Arguments}");
					p.Start ();
					p.BeginOutputReadLine ();
					p.BeginErrorReadLine ();
					ranToCompletion = p.WaitForExit ((int)new TimeSpan (0, 15, 0).TotalMilliseconds);
					if (psi.RedirectStandardOutput)
						stdout.WaitOne ();
					if (psi.RedirectStandardError)
						err.WaitOne ();
					result = ranToCompletion && p.ExitCode == 0;
					if (processLog != null) {
						if (ranToCompletion) {
							File.AppendAllText (processLog, $"ExitCode: {p.ExitCode}{Environment.NewLine}");
						} else {
							File.AppendAllText (processLog, $"Build Timed Out!{Environment.ExitCode}");
						}
					}
				}

				LastBuildTime = DateTime.UtcNow - start;

				if (buildLogFullPath != null && File.Exists (buildLogFullPath)) {
					foreach (var line in LastBuildOutput) {
						if (line.StartsWith ("Time Elapsed", StringComparison.OrdinalIgnoreCase)) {
							var match = timeElapsedRegEx.Match (line);
							if (match.Success) {
								LastBuildTime = TimeSpan.Parse (match.Groups ["TimeSpan"].Value);
								Console.WriteLine ($"Found Time Elapsed {LastBuildTime}");
							}
						}
					}
				}

				if (nativeCrashDetected) {
					Console.WriteLine ($"Native crash detected! Running the build for {projectOrSolution} again.");
					if (attempt == 0)
						File.Move (processLog, processLog + ".bak");
					nativeCrashDetected = false;
					continue;
				} else {
					break;
				}
			}


			if (buildLogFullPath != null && processLog != null) {
				Directory.CreateDirectory (Path.GetDirectoryName (buildLogFullPath));
				if (File.Exists (processLog))
					File.AppendAllText (buildLogFullPath, File.ReadAllText (processLog));
			}
			if (!result && ThrowOnBuildFailure) {
				string message = "Build failure: " + Path.GetFileName (projectOrSolution) + (BuildLogFile != null && File.Exists (buildLogFullPath) ? "Build log recorded at " + buildLogFullPath : null);
				//NOTE: enormous logs will lock up IDE's UI. Build result files should be appended to the TestResult on failure.
				throw new FailedBuildException (message);
			}

			return result;
		}

		bool IsRunningInIDE {
			get {
				//Check for Windows, process is testhost.x86 in VS 2017
				using (var p = Process.GetCurrentProcess ()) {
					if (p.ProcessName.IndexOf ("testhost", StringComparison.OrdinalIgnoreCase) != -1) {
						return true;
					}
				}

				//Check for macOS, value is normally /Applications/Visual Studio.app/Contents/Resources
				var gac_prefix = Environment.GetEnvironmentVariable ("MONO_GAC_PREFIX", EnvironmentVariableTarget.Process);
				if (!string.IsNullOrEmpty (gac_prefix) && gac_prefix.IndexOf ("Visual Studio", StringComparison.OrdinalIgnoreCase) != -1) {
					return true;
				}

				return false;
			}
		}

		string QuoteFileName (string fileName)
		{
			return fileName.Contains (" ") ? $"\"{fileName}\"" : fileName;
		}

	}
}

