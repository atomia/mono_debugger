using System;
using SD = System.Diagnostics;
using ST = System.Threading;
using NUnit.Framework;

using Mono.Debugger;
using Mono.Debugger.Languages;
using Mono.Debugger.Frontend;
using Mono.Debugger.Test.Framework;

namespace Mono.Debugger.Tests
{
	[DebuggerTestFixture(Timeout = 10000)]
	public class TestAttach : DebuggerTestFixture
	{
		SD.Process child;

		public TestAttach ()
			: base ("TestAttach")
		{ }

		public override void SetUp ()
		{
			base.SetUp ();

			SD.ProcessStartInfo start = new SD.ProcessStartInfo (
				MonoExecutable, "--debug -O=-gshared " + ExeFileName);

			start.UseShellExecute = false;
			start.RedirectStandardOutput = true;
			start.RedirectStandardError = true;

			child = SD.Process.Start (start);
			child.StandardOutput.ReadLine ();
		}

		public override void TearDown ()
		{
			base.TearDown ();

			if (!child.HasExited)
				child.Kill ();
			child.WaitForExit ();
		}

		[Test]
		[Category("Attach")]
		public void Main ()
		{
			Process process = Attach (child.Id);
			Assert.IsTrue (process.MainThread.IsStopped);

			AssertThreadCreated ();

			AssertStopped (null, null, -1);

			StackFrame frame = process.MainThread.CurrentFrame;
			Assert.IsNotNull (frame);
			Backtrace bt = process.MainThread.GetBacktrace (-1);
			if (bt.Count < 1)
				Assert.Fail ("Cannot get backtrace.");

			process.Detach ();
			AssertTargetExited (process);
		}

		[Test]
		[Category("Attach")]
		public void AttachAgain ()
		{
			Process process = Attach (child.Id);
			Assert.IsTrue (process.MainThread.IsStopped);

			AssertThreadCreated ();

			AssertStopped (null, null, -1);

			StackFrame frame = process.MainThread.CurrentFrame;
			Assert.IsNotNull (frame);
			Backtrace bt = process.MainThread.GetBacktrace (-1);
			if (bt.Count < 1)
				Assert.Fail ("Cannot get backtrace.");

			process.Detach ();
			AssertTargetExited (process);
		}

		[Test]
		[Category("Attach")]
		public void Kill ()
		{
			Process process = Attach (child.Id);
			Assert.IsTrue (process.MainThread.IsStopped);

			AssertThreadCreated ();

			AssertStopped (null, null, -1);

			StackFrame frame = process.MainThread.CurrentFrame;
			Assert.IsNotNull (frame);
			Backtrace bt = process.MainThread.GetBacktrace (-1);
			if (bt.Count < 1)
				Assert.Fail ("Cannot get backtrace.");

			process.Kill ();
			AssertTargetExited (process);
		}
	}
}
