using System;
using NUnit.Framework;

using Mono.Debugger;
using Mono.Debugger.Languages;
using Mono.Debugger.Frontend;

namespace Mono.Debugger.Tests
{
	[TestFixture]
	public class TestExec : TestSuite
	{
		public TestExec ()
			: base ("TestExec.exe", "TestExec.cs",
				BuildDirectory + "/testnativechild")
		{ }

		const int line_main = 8;
		const int line_main_2 = 10;

		int bpt_main;

		[Test]
		[Category("Fork")]
		public void NativeChild ()
		{
			Process process = Interpreter.Start ();
			Assert.IsTrue (process.IsManaged);
			Assert.IsTrue (process.MainThread.IsStopped);
			Thread thread = process.MainThread;

			AssertStopped (thread, "X.Main(System.String[])", line_main);
			bpt_main = AssertBreakpoint (line_main_2);
			AssertExecute ("continue");
			AssertHitBreakpoint (thread, bpt_main, "X.Main(System.String[])", line_main_2);

			AssertExecute ("next");

			Thread child = AssertProcessCreated ();
			Thread execd_child = null;

			bool exited = false;
			bool execd = false;
			bool stopped = false;
			bool thread_created = false;
			bool child_exited = false;

			while (!stopped || !thread_created || !exited || !execd || !child_exited) {
				DebuggerEvent e = AssertEvent ();

				if (e.Type == DebuggerEventType.ProcessExited) {
					if ((Process) e.Data == child.Process) {
						child_exited = true;
						continue;
					}
				} else if (e.Type == DebuggerEventType.ProcessExecd) {
					if ((Process) e.Data == child.Process) {
						execd = true;
						continue;
					}
				} else if (e.Type == DebuggerEventType.ThreadCreated) {
					execd_child = (Thread) e.Data;
					thread_created = true;
					continue;
				} else if (e.Type == DebuggerEventType.TargetEvent) {
					Thread e_thread = (Thread) e.Data;
					TargetEventArgs args = (TargetEventArgs) e.Data2;

					if ((e_thread == thread) &&
					    (args.Type == TargetEventType.TargetStopped)) {
						stopped = true;
						continue;
					} else if ((e_thread == execd_child) &&
						   (args.Type == TargetEventType.TargetExited)) {
						exited = true;
						continue;
					}
				}

				Assert.Fail ("Received unexpected event {0}", e);
			}

			AssertFrame (thread, "X.Main(System.String[])", line_main_2 + 1);
			AssertExecute ("next");
			AssertTargetOutput ("Hello World!");
			AssertStopped (thread, "X.Main(System.String[])", line_main_2 + 2);

			AssertExecute ("continue");
			AssertProcessExited (thread.Process);
			AssertTargetExited ();
		}

		[Test]
		[Category("Fork")]
		public void ManagedChild ()
		{
			Interpreter.Options.InferiorArgs = new string [] {
				BuildDirectory + "/TestExec.exe",
				MonoExecutable, BuildDirectory + "/TestChild.exe" };

			Process process = Interpreter.Start ();
			Assert.IsTrue (process.IsManaged);
			Assert.IsTrue (process.MainThread.IsStopped);
			Thread thread = process.MainThread;

			AssertStopped (thread, "X.Main(System.String[])", line_main);
			AssertExecute ("continue");
			AssertHitBreakpoint (thread, bpt_main, "X.Main(System.String[])", line_main_2);

			AssertExecute ("next");

			Thread child = AssertProcessCreated ();
			Thread execd_child = null;

			bool exited = false;
			bool execd = false;
			bool stopped = false;
			bool thread_created = false;
			bool child_exited = false;

			while (!stopped || !thread_created || !exited || !execd || !child_exited) {
				DebuggerEvent e = AssertEvent ();

				if (e.Type == DebuggerEventType.ProcessExited) {
					if ((Process) e.Data == child.Process) {
						child_exited = true;
						continue;
					}
				} else if (e.Type == DebuggerEventType.ProcessExecd) {
					if ((Process) e.Data == child.Process) {
						execd = true;
						continue;
					}
				} else if (e.Type == DebuggerEventType.ThreadCreated) {
					execd_child = (Thread) e.Data;
					thread_created = true;
					continue;
				} else if (e.Type == DebuggerEventType.TargetEvent) {
					Thread e_thread = (Thread) e.Data;
					TargetEventArgs args = (TargetEventArgs) e.Data2;

					if ((e_thread == thread) &&
					    (args.Type == TargetEventType.TargetStopped)) {
						stopped = true;
						continue;
					} else if ((e_thread == execd_child) &&
						   (args.Type == TargetEventType.TargetExited)) {
						exited = true;
						continue;
					}
				}

				Assert.Fail ("Received unexpected event {0}", e);
			}

			AssertFrame (thread, "X.Main(System.String[])", line_main_2 + 1);
			AssertExecute ("next");
			AssertTargetOutput ("Hello World");
			AssertStopped (thread, "X.Main(System.String[])", line_main_2 + 2);

			AssertExecute ("continue");
			AssertProcessExited (thread.Process);
			AssertTargetExited ();
		}
	}
}
