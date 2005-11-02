using System;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Runtime.InteropServices;

using Mono.Debugger.Remoting;

namespace Mono.Debugger
{
	[Flags]
	public enum DebugFlags {
		None			= 0,
		JitSymtab		= 1,
		MethodAddress		= 2,
		Threads			= 4,
		Signals			= 8,
		EventLoop		= 16,
		Wait			= 32,
		SSE			= 64,
		Notification		= 128,
		Mutex			= 256,
		SymbolTable		= 512,
		SourceFiles		= 1024,
		DwarfReader             = 2048,
		Remoting		= 4096
	}

	public static class Report
	{
		[Conditional("DEBUG")]
		public static void Debug (DebugFlags category, object argument)
		{
			Debug (category, "{0}", argument);
		}

		[Conditional("DEBUG")]
		public static void Debug (DebugFlags category, string message, params object[] args)
		{
			string formatted = String.Format (message, args);
			DebuggerContext.ReportWriter.Debug (category, formatted);
		}
	}

	public class ReportWriter : MarshalByRefObject
	{
		int flags;
		StreamWriter writer;

		public ReportWriter ()
		{
			string file = Environment.GetEnvironmentVariable ("MDB_DEBUG_OUTPUT");
			if (file != null)
				writer = new StreamWriter (file, true);
			else
				writer = new StreamWriter (Console.OpenStandardError ());
			writer.AutoFlush = true;

			string var = Environment.GetEnvironmentVariable ("MDB_DEBUG_FLAGS");
			if (var != null) {
				try {
					flags = Int32.Parse (var);
				} catch {
					Console.WriteLine (
						"Invalid `MDB_DEBUG_FLAGS' environment " +
						"variable.");
				}
			}
		}

		public ReportWriter (string file, DebugFlags flags)
		{
			this.flags = (int) flags;

			if (file != null)
				writer = new StreamWriter (file, true);
			else
				writer = new StreamWriter (Console.OpenStandardError ());
			writer.AutoFlush = true;
		}

		public void Debug (DebugFlags category, string message)
		{
			if (((int) category & (int) flags) == 0)
				return;

			writer.WriteLine (message);
		}
	}
}
