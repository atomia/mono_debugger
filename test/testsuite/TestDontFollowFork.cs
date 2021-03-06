using System;
using System.IO;
using NUnit.Framework;

using Mono.Debugger;
using Mono.Debugger.Languages;
using Mono.Debugger.Frontend;
using Mono.Debugger.Test.Framework;

namespace Mono.Debugger.Tests
{
	[DebuggerTestFixture(Timeout = 15000)]
	public class TestDontFollowFork : DebuggerTestFixture
	{
		public TestDontFollowFork ()
			: base ("TestExec.exe", "TestExec.cs")
		{ }

		const int line_main = 8;
		const int line_main_3 = 12;

		int bpt_main;

		public override void SetUp ()
		{
			base.SetUp ();
			Config.FollowFork = false;

			bpt_main = AssertBreakpoint (
				String.Format ("-local {0}:{1}", FileName, line_main_3));
		}

		[Test]
		[Category("Native")]
		[Category("Fork")]
		public void NativeChild ()
		{
			Interpreter.Options.File = Path.Combine (BuildDirectory, "TestExec.exe");
			Interpreter.Options.InferiorArgs = new string [] { Path.Combine (BuildDirectory, "testnativechild") };

			Process process = Start ();
			Assert.IsTrue (process.IsManaged);
			Assert.IsTrue (process.MainThread.IsStopped);
			Thread thread = process.MainThread;

			AssertStopped (thread, "X.Main(string[])", line_main);
			AssertExecute ("continue -wait");

			AssertHitBreakpoint (thread, bpt_main,"X.Main(string[])", line_main_3);

			AssertPrint (thread, "process.ExitCode", "(int) 0");
			AssertTargetOutput ("Hello World!");
			AssertExecute ("continue");
			AssertTargetExited (thread.Process);
		}

		[Test]
		[Category("Fork")]
		public void ManagedChild ()
		{
			Interpreter.Options.File = Path.Combine (BuildDirectory, "TestExec.exe");
			Interpreter.Options.InferiorArgs = new string [] {
				MonoExecutable, Path.Combine (BuildDirectory, "TestChild.exe")
			};

			Process process = Start ();
			Assert.IsTrue (process.IsManaged);
			Assert.IsTrue (process.MainThread.IsStopped);
			Thread thread = process.MainThread;

			AssertStopped (thread, "X.Main(string[])", line_main);
			AssertExecute ("continue -wait");

			AssertHitBreakpoint (thread, bpt_main,"X.Main(string[])", line_main_3)
;
			AssertPrint (thread, "process.ExitCode", "(int) 0");
			AssertTargetOutput ("Hello World");
			AssertExecute ("continue");
			AssertTargetExited (thread.Process);
		}
	}
}
