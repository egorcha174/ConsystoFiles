// Copyright (c) Files Community
// SPDX-License-Identifier: MPL-2.0

using Windows.Win32;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;

namespace Files.App.Services
{
	/// <inheritdoc cref="IWindowsSecurityService"/>
	public sealed class WindowsSecurityService : IWindowsSecurityService
	{
		/// <inheritdoc/>
		public unsafe bool IsAppElevated()
		{
			var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
			var principal = new System.Security.Principal.WindowsPrincipal(identity);
			return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
		}

		/// <inheritdoc/>
		public unsafe bool CanDragAndDrop()
		{
			// Consysto fork: UIPI only blocks drops coming from a lower integrity level. When Explorer itself runs with the
			// administrator token (built-in Administrator account, or the whole shell started elevated) Files and Explorer
			// are on the same level and drag & drop works, so it stays off only when Files is elevated above the shell.
			return !IsAppElevated() || IsShellElevated();
		}

		private static unsafe bool IsShellElevated()
		{
			try
			{
				var shellWindow = PInvoke.GetShellWindow();
				uint processId = 0;
				if (shellWindow.IsNull || PInvoke.GetWindowThreadProcessId(shellWindow, &processId) == 0 || processId == 0)
					return false;

				var processHandle = PInvoke.OpenProcess(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
				using var process = new Microsoft.Win32.SafeHandles.SafeFileHandle((nint)processHandle.Value, ownsHandle: true);
				if (process.IsInvalid || !PInvoke.OpenProcessToken(process, TOKEN_ACCESS_MASK.TOKEN_QUERY | TOKEN_ACCESS_MASK.TOKEN_DUPLICATE, out var token))
					return false;

				using (token)
				using (var identity = new System.Security.Principal.WindowsIdentity(token.DangerousGetHandle()))
					return new System.Security.Principal.WindowsPrincipal(identity).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
			}
			catch (Exception)
			{
				return false;
			}
		}

		/// <inheritdoc/>
		public bool IsElevationRequired(string? path)
		{
			if (string.IsNullOrEmpty(path))
				return false;

			return Win32PInvoke.IsElevationRequired(path);
		}
	}
}
