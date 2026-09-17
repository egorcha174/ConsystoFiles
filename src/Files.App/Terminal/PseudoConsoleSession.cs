// Consysto fork: a shell running in a Windows pseudo console (ConPTY), for the terminal tab.

using Microsoft.Win32.SafeHandles;
using System.Runtime.InteropServices;
using System.IO;
using System.Text;

namespace Files.App.Terminal
{
	/// <summary>
	/// One shell process attached to a pseudo console. Output arrives as UTF-8 text with VT sequences, exactly what a
	/// terminal emulator draws; input is the keys the emulator produces.
	/// </summary>
	public sealed partial class PseudoConsoleSession : IDisposable
	{
		private const uint ExtendedStartupInfoPresent = 0x00080000;
		private const uint CreateUnicodeEnvironment = 0x00000400;
		private const nint ProcThreadAttributePseudoConsole = 0x00020016;
		private const uint Infinite = 0xFFFFFFFF;

		private nint pseudoConsole;
		private nint attributeList;
		private nint processHandle;
		private nint threadHandle;
		private readonly FileStream input;
		private readonly FileStream output;
		private int disposed;

		/// <summary>Output text, raised on a background thread.</summary>
		public event Action<string>? OutputReceived;

		/// <summary>The shell has exited, raised on a background thread.</summary>
		public event Action? Exited;

		private PseudoConsoleSession(nint pseudoConsole, FileStream input, FileStream output)
		{
			this.pseudoConsole = pseudoConsole;
			this.input = input;
			this.output = output;
		}

		public static unsafe PseudoConsoleSession Start(string commandLine, string workingDirectory, short columns, short rows)
		{
			if (!CreatePipe(out var inputRead, out var inputWrite, 0, 0) || !CreatePipe(out var outputRead, out var outputWrite, 0, 0))
				throw new InvalidOperationException("Pipes for the pseudo console could not be created.");

			var result = CreatePseudoConsole(new Coord(columns, rows), inputRead, outputWrite, 0, out var console);
			if (result != 0)
				throw Marshal.GetExceptionForHR(result)!;

			var session = new PseudoConsoleSession(
				console,
				new FileStream(new SafeFileHandle(inputWrite, true), FileAccess.Write, 1),
				new FileStream(new SafeFileHandle(outputRead, true), FileAccess.Read, 1));

			try
			{
				nint size = 0;
				InitializeProcThreadAttributeList(0, 1, 0, ref size);
				session.attributeList = Marshal.AllocHGlobal(size);
				if (!InitializeProcThreadAttributeList(session.attributeList, 1, 0, ref size)
					|| !UpdateProcThreadAttribute(session.attributeList, 0, ProcThreadAttributePseudoConsole, console, sizeof(nint), 0, 0))
					throw new InvalidOperationException("The pseudo console could not be attached to the process.");

				var startupInfo = new StartupInfoEx { AttributeList = session.attributeList };
				startupInfo.StartupInfo.Size = sizeof(StartupInfoEx);

				var command = (commandLine + "\0").ToCharArray();
				fixed (char* commandPointer = command)
				fixed (char* directoryPointer = workingDirectory)
				{
					if (!CreateProcessW(0, commandPointer, 0, 0, false, ExtendedStartupInfoPresent | CreateUnicodeEnvironment, 0,
						Directory.Exists(workingDirectory) ? directoryPointer : null, &startupInfo, out var process))
						throw new InvalidOperationException($"The shell could not be started: {Marshal.GetLastPInvokeErrorMessage()}");

					session.processHandle = process.Process;
					session.threadHandle = process.Thread;
				}
			}
			catch
			{
				session.Dispose();
				throw;
			}
			finally
			{
				// The console holds its own ends; ours would keep the pipes open after the shell exits.
				CloseHandle(inputRead);
				CloseHandle(outputWrite);
			}

			new Thread(session.ReadOutput) { IsBackground = true, Name = "Terminal output" }.Start();
			new Thread(session.WaitForExit) { IsBackground = true, Name = "Terminal exit" }.Start();
			return session;
		}

		public void Write(string text)
		{
			if (disposed != 0)
				return;

			try
			{
				var bytes = Encoding.UTF8.GetBytes(text);
				input.Write(bytes);
				input.Flush();
			}
			catch (IOException)
			{
				// The shell has gone; the exit event tells the page.
			}
		}

		public void Resize(short columns, short rows)
		{
			if (disposed == 0 && columns > 0 && rows > 0)
				ResizePseudoConsole(pseudoConsole, new Coord(columns, rows));
		}

		private void ReadOutput()
		{
			var buffer = new byte[8192];
			var decoder = Encoding.UTF8.GetDecoder();
			var chars = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
			try
			{
				int read;
				while ((read = output.Read(buffer, 0, buffer.Length)) > 0)
				{
					// The decoder keeps a character split between two reads.
					var count = decoder.GetChars(buffer, 0, read, chars, 0);
					if (count > 0)
						OutputReceived?.Invoke(new string(chars, 0, count));
				}
			}
			catch (Exception ex) when (ex is IOException or ObjectDisposedException)
			{
			}
		}

		private void WaitForExit()
		{
			var process = processHandle;
			if (process != 0)
			{
				WaitForSingleObject(process, Infinite);
				CloseHandle(process);
			}

			if (disposed == 0)
				Exited?.Invoke();
		}

		public void Dispose()
		{
			if (Interlocked.Exchange(ref disposed, 1) != 0)
				return;

			// Closing the console ends the shell and breaks the output pipe, which stops the reader.
			if (pseudoConsole != 0)
				ClosePseudoConsole(pseudoConsole);
			pseudoConsole = 0;

			input.Dispose();
			output.Dispose();

			if (attributeList != 0)
			{
				DeleteProcThreadAttributeList(attributeList);
				Marshal.FreeHGlobal(attributeList);
				attributeList = 0;
			}
			if (threadHandle != 0)
				CloseHandle(threadHandle);
			threadHandle = 0;
			// The exit waiter still holds the process handle; it ends when the process does.
		}

		[StructLayout(LayoutKind.Sequential)]
		private readonly struct Coord(short x, short y)
		{
			public readonly short X = x;
			public readonly short Y = y;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct StartupInfo
		{
			public int Size;
			public nint Reserved, Desktop, Title;
			public int X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
			public short ShowWindow, Reserved2;
			public nint Reserved2Pointer, StdInput, StdOutput, StdError;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct StartupInfoEx
		{
			public StartupInfo StartupInfo;
			public nint AttributeList;
		}

		[StructLayout(LayoutKind.Sequential)]
		private struct ProcessInformation
		{
			public nint Process, Thread;
			public int ProcessId, ThreadId;
		}

		[LibraryImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static partial bool CreatePipe(out nint readPipe, out nint writePipe, nint attributes, uint size);

		[LibraryImport("kernel32.dll")]
		private static partial int CreatePseudoConsole(Coord size, nint input, nint output, uint flags, out nint console);

		[LibraryImport("kernel32.dll")]
		private static partial int ResizePseudoConsole(nint console, Coord size);

		[LibraryImport("kernel32.dll")]
		private static partial void ClosePseudoConsole(nint console);

		[LibraryImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static partial bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

		[LibraryImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static partial bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returned);

		[LibraryImport("kernel32.dll")]
		private static partial void DeleteProcThreadAttributeList(nint list);

		[LibraryImport("kernel32.dll", SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static unsafe partial bool CreateProcessW(nint application, char* commandLine, nint processAttributes, nint threadAttributes,
			[MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, nint environment, char* currentDirectory,
			StartupInfoEx* startupInfo, out ProcessInformation processInformation);

		[LibraryImport("kernel32.dll")]
		private static partial uint WaitForSingleObject(nint handle, uint milliseconds);

		[LibraryImport("kernel32.dll")]
		[return: MarshalAs(UnmanagedType.Bool)]
		private static partial bool CloseHandle(nint handle);
	}
}
