﻿using System;
using System.ComponentModel.Composition;
using System.IO;
using System.Net;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Forms;
using Sledge.Common.Shell.Commands;
using Sledge.Common.Shell.Context;
using Sledge.Common.Shell.Menu;
using Sledge.Common.Translations;
using Sledge.Shell;

namespace Sledge.Editor.Update
{
	[AutoTranslate]
	[Export(typeof(ICommand))]
	[MenuItem("Help", "", "Update", "B")]
	[CommandID("Sledge:Editor:CheckForUpdates")]
	public class CheckForUpdates : ICommand
	{
		private readonly Form _shell;
		private readonly ITranslationStringProvider _translation;

		public string Name { get; set; } = "Check for updates";
		public string Details { get; set; } = "Check online for updates";

		public string NoUpdatesTitle { get; set; } = "No updates found";
		public string NoUpdatesMessage { get; set; } = "This version of Sledge is currently up-to-date.";

		public string UpdateErrorTitle { get; set; } = "Update error";
		public string UpdateErrorMessage { get; set; } = "Error downloading the update details.";

		private const string GithubReleasesApiUrl = "https://api.github.com/repos/Duude92/hammertime/releases?page=1";

		[ImportingConstructor]
		public CheckForUpdates(
			[Import("Shell")] Form shell,
			[Import] ITranslationStringProvider translation
		)
		{
			_shell = shell;
			_translation = translation;
			ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
		}

		public bool IsInContext(IContext context)
		{
			return true;
		}

		public async Task Invoke(IContext context, CommandParameters parameters)
		{
			var silent = parameters.Get("Silent", false);
			var buildInfo = GetBuildInfo();

			var details = await GetLatestReleaseDetails(buildInfo.Tag);

			if (!details.Exists)
			{
				if (!silent)
				{
					_shell.InvokeLater(() =>
					{
						MessageBox.Show(UpdateErrorMessage, UpdateErrorTitle, MessageBoxButtons.OK, MessageBoxIcon.Error);
					});
				}
				return;
			}
			if (details.PublishDate < (buildInfo.BuildTime.AddMinutes(15)))
			{
				if (!silent)
				{
					_shell.InvokeLater(() =>
					{
						MessageBox.Show("Your application is up to date.", "Update is not required", MessageBoxButtons.OK, MessageBoxIcon.Information);
					});
				}
				return;
			}

			_shell.InvokeLater(() =>
			{
				var form = new UpdaterForm(details, _translation);
				form.Show(_shell);
			});
		}
		private BuildInfo GetBuildInfo()
		{
			var path = "./build";
			if (!File.Exists(path)) return new BuildInfo { BuildTime = new DateTime(), Tag = "latest" };
			using (TextReader reader = new StreamReader(path))
			{
				return new BuildInfo
				{
					BuildTime = DateTime.ParseExact(reader.ReadLine(), "yyyy-MM-dd HH:mm", null),
					Tag = reader.ReadLine(),
				};
			}
		}
		private DateTime GetBuildTime()
		{
			var path = "./build";
			if (!File.Exists(path)) return new DateTime();
			string datetimeStr = null;
			using (TextReader reader = new StreamReader(path))
			{
				datetimeStr = reader.ReadLine();
			}

			DateTime datetime = DateTime.ParseExact(datetimeStr, "yyyy-MM-dd HH:mm", null);
			return datetime;
		}

		private Version GetCurrentVersion()
		{
			return typeof(Program).Assembly.GetName().Version;
		}

		private async Task<UpdateReleaseDetails> GetLatestReleaseDetails(string tag)
		{
			using (var wc = new WebClient())
			{
				try
				{
					wc.Headers.Add(HttpRequestHeader.UserAgent, "Duude92/hammertime");
					var str = await wc.DownloadStringTaskAsync(GithubReleasesApiUrl);
					return new UpdateReleaseDetails(str, tag);
				}
				catch (WebException ex)
				{
					if (ex.Status == WebExceptionStatus.ProtocolError)
					{
						MessageBox.Show("Github WebApi is unavailable.\nYou can try to check for updates later, or download manually from GitHub.");
					}
					MessageBox.Show(ex.Message);
					return null;
				}
			}
		}

		private class UpdateCheckResult
		{
			public Version Version { get; set; }
			public DateTime Date { get; set; }
		}
		private class BuildInfo
		{
			public string Tag { get; set; }
			public DateTime BuildTime { get; set; }
		}
	}
}