using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.DotNet.XHarness.iOS.Shared.Execution;
using Microsoft.DotNet.XHarness.iOS.Shared.Logging;
using Microsoft.DotNet.XHarness.iOS.Shared.Tasks;
using Microsoft.DotNet.XHarness.iOS.Shared.Utilities;

using P = System.IO.Path;

namespace Microsoft.DotNet.XHarness.iOS.Shared {
	public class TestProject
	{
		XmlDocument xml;

		public string Path;
		public string SolutionPath;
		public string Name;
		public bool IsExecutableProject;
		public bool IsNUnitProject;
		public bool IsDotNetProject;
		public string [] Configurations;
		public Func<Task> Dependency;
		public string FailureMessage;
		public bool RestoreNugetsInProject;
		public string MTouchExtraArgs;
		public double TimeoutMultiplier = 1;

		public IEnumerable<TestProject> ProjectReferences;

		// Optional
		public MonoNativeInfo MonoNativeInfo { get; set; }

		public TestProject ()
		{
		}

		public TestProject (string path, bool isExecutableProject = true)
		{
			Path = path;
			IsExecutableProject = isExecutableProject;
		}

		public XmlDocument Xml {
			get {
				if (xml == null) {
					xml = new XmlDocument ();
					xml.LoadWithoutNetworkAccess (Path);
				}
				return xml;
			}
		}

		public virtual TestProject Clone ()
		{
			TestProject rv = (TestProject) Activator.CreateInstance (GetType ());
			rv.Path = Path;
			rv.IsExecutableProject = IsExecutableProject;
			rv.RestoreNugetsInProject = RestoreNugetsInProject;
			rv.Name = Name;
			rv.MTouchExtraArgs = MTouchExtraArgs;
			rv.TimeoutMultiplier = TimeoutMultiplier;
			rv.IsDotNetProject = IsDotNetProject;
			return rv;
		}

		internal async Task<TestProject> CreateCloneAsync (ILog log, IProcessManager processManager, ITestTask test)
		{
			var rv = Clone ();
			await rv.CreateCopyAsync (log, processManager, test);
			return rv;
		}

		public async Task CreateCopyAsync (ILog log, IProcessManager processManager, ITestTask test = null)
		{
			var directory = DirectoryUtilities.CreateTemporaryDirectory (test?.TestName ?? System.IO.Path.GetFileNameWithoutExtension (Path));
			Directory.CreateDirectory (directory);
			var original_path = Path;
			Path = System.IO.Path.Combine (directory, System.IO.Path.GetFileName (Path));

			await Task.Yield ();

			XmlDocument doc;
			doc = new XmlDocument ();
			doc.LoadWithoutNetworkAccess (original_path);
			var original_name = System.IO.Path.GetFileName (original_path);
			if (original_name.Contains ("GuiUnit_NET") || original_name.Contains ("GuiUnit_xammac_mobile")) {
				// The GuiUnit project files writes stuff outside their project directory using relative paths,
				// but override that so that we don't end up with multiple cloned projects writing stuff to
				// the same location.
				doc.SetOutputPath ("bin\\$(Configuration)");
				doc.SetNode ("DocumentationFile", "bin\\$(Configuration)\\nunitlite.xml");
			}
			doc.ResolveAllPaths (original_path);
			if (doc.IsDotNetProject ()) {
				// Many types of files below the csproj directory are included by default,
				// which means that we have to include them manually in the cloned csproj,
				// because it's in a very different directory.
				var test_dir = P.GetDirectoryName (original_path);

				// Get all the files in the project directory from git
				using var process = new Process ();
				process.StartInfo.FileName = "git";
				process.StartInfo.Arguments = "ls-files";
				process.StartInfo.WorkingDirectory = test_dir;
				var stdout = new MemoryLog () { Timestamp = false };
				var result = await processManager.RunAsync (process, log, stdout, stdout, timeout: TimeSpan.FromSeconds (15));
				if (!result.Succeeded)
					throw new Exception ($"Failed to list the files in the directory {test_dir} (TimedOut: {result.TimedOut} ExitCode: {result.ExitCode}):\n{stdout}");

				var files = stdout.ToString ().Split ('\n');
				foreach (var file in files) {
					var ext = P.GetExtension (file);
					var full_path = P.Combine (test_dir, file);
					var windows_file = full_path.Replace ('/', '\\');

					if (file.Contains (".xcasset")) {
						doc.AddInclude ("ImageAsset", file, windows_file, true);
						continue;
					}

					switch (ext.ToLowerInvariant ()) {
					case ".cs":
						doc.AddInclude ("Compile", file, windows_file, true);
						break;
					case ".plist":
						doc.AddInclude ("None", file, windows_file, true);
						break;
					case ".storyboard":
						doc.AddInclude ("InterfaceDefinition", file, windows_file, true);
						break;
					case ".gitignore":
					case ".csproj":
					case "": // Makefile
						break; // ignore these files
					default:
						Console.WriteLine ($"Unknown file: {file} (extension: {ext}). There might be a default inclusion behavior for this file.");
						break;
					}
				}
			}
			
			var projectReferences = new List<TestProject> ();
			foreach (var pr in doc.GetProjectReferences ()) {
				var tp = new TestProject (pr.Replace ('\\', '/'));
				await tp.CreateCopyAsync (log, processManager, test);
				doc.SetProjectReferenceInclude (pr, tp.Path.Replace ('/', '\\'));
				projectReferences.Add (tp);
			}
			this.ProjectReferences = projectReferences;

			doc.Save (Path);
		}

		public override string ToString()
		{
			return Name;
		}
	}

}

