using System;
using System.IO;
using System.Configuration;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ST = System.Threading;

using Mono.Debugger;
using Mono.Debugger.Backend;

namespace Mono.Debugger.Frontend
{
	public class CommandLineInterpreter
	{
		public string Prompt {
			get {
				if (main_loop_stack.Count == 1)
					return "(mdb) ";
				else
					return String.Format ("(nested-break level {0}) ", main_loop_stack.Count-1);
			}
		}

		public Interpreter Interpreter {
			get { return interpreter; }
		}

		public ST.AutoResetEvent InterruptEvent {
			get { return interrupt_event; }
		}

		public ST.AutoResetEvent EnterNestedBreakStateEvent {
			get { return enter_nested_break_state_event; }
		}

		Interpreter interpreter;
		DebuggerEngine engine;
		LineParser parser;
		int line = 0;

		bool is_inferior_main;
		ST.Thread interrupt_thread;
		ST.Thread main_thread;

		ST.AutoResetEvent interrupt_event;
		ST.AutoResetEvent enter_nested_break_state_event;

		Stack<CommandLineInterpreter.MainLoop> main_loop_stack;

		[DllImport("monodebuggerserver")]
		static extern int mono_debugger_server_static_init ();

		[DllImport("monodebuggerserver")]
		static extern int mono_debugger_server_get_pending_sigint ();

		static CommandLineInterpreter ()
		{
			mono_debugger_server_static_init ();
		}

		internal CommandLineInterpreter (bool is_interactive, DebuggerConfiguration config,
						 DebuggerOptions options)
		{
			if (options.HasDebugFlags)
				Report.Initialize (options.DebugOutput, options.DebugFlags);
			else
				Report.Initialize ();

			interpreter = new Interpreter (is_interactive, config, options);
			interpreter.CLI = this;

			engine = interpreter.DebuggerEngine;
			parser = new LineParser (engine);

			interrupt_event = new ST.AutoResetEvent (false);
			enter_nested_break_state_event = new ST.AutoResetEvent (false);

			main_loop_stack = new Stack<MainLoop> ();
			main_loop_stack.Push (new MainLoop (interpreter));

			main_thread = new ST.Thread (new ST.ThreadStart (main_thread_main));
			main_thread.IsBackground = true;

			interrupt_thread = new ST.Thread (new ST.ThreadStart (interrupt_thread_main));
			interrupt_thread.IsBackground = true;
		}

		public CommandLineInterpreter (Interpreter interpreter)
		{
			this.interpreter = interpreter;
			this.engine = interpreter.DebuggerEngine;

			interpreter.CLI = this;
			parser = new LineParser (engine);

			interrupt_event = new ST.AutoResetEvent (false);
			enter_nested_break_state_event = new ST.AutoResetEvent (false);

			main_loop_stack = new Stack<MainLoop> ();
			main_loop_stack.Push (new MainLoop (interpreter));

			main_thread = new ST.Thread (new ST.ThreadStart (main_thread_main));
			main_thread.IsBackground = true;

			interrupt_thread = new ST.Thread (new ST.ThreadStart (interrupt_thread_main));
			interrupt_thread.IsBackground = true;
		}

		public void DoRunMainLoop ()
		{
			string s;
			bool is_complete = true;

			parser.Reset ();
			while ((s = ReadInput (is_complete)) != null) {
				MainLoop loop = main_loop_stack.Peek ();

				interpreter.ClearInterrupt ();

				if (s == "") {
					if (!is_complete || !loop.Repeat ())
						continue;

					wait_for_completion ();

					parser.Reset ();
					is_complete = true;
					continue;
				}

				parser.Append (s);
				if (!parser.IsComplete ()) {
					is_complete = false;
					continue;
				}

				Command command = parser.GetCommand ();
				if (command == null)
					interpreter.Error ("No such command `{0}'.", s);
				else {
					loop.ExecuteCommand (command);
					wait_for_completion ();
				}

				parser.Reset ();
				is_complete = true;
			}
		}

		void execute_command (Command command)
		{
			MainLoop loop = main_loop_stack.Peek ();
			loop.ExecuteCommand (command);

			wait_for_completion ();
		}

		void wait_for_completion ()
		{
		again:
			MainLoop loop = main_loop_stack.Peek ();

			Report.Debug (DebugFlags.CLI, "{0} waiting for completion", loop);

			ST.WaitHandle[] wait = new ST.WaitHandle[] {
				loop.CompletedEvent, interrupt_event, enter_nested_break_state_event
			};
			int ret = ST.WaitHandle.WaitAny (wait);

			Report.Debug (DebugFlags.CLI, "{0} waiting for completion done: {1}", loop, ret);

			if (loop.Completed) {
				main_loop_stack.Pop ();
				goto again;
			}
		}

		public void EnterNestedBreakState ()
		{
			MainLoop loop = new MainLoop (interpreter);
			main_loop_stack.Push (loop);

			Report.Debug (DebugFlags.CLI, "{0} enter nested break state", loop);

			enter_nested_break_state_event.Set ();
		}

		public void LeaveNestedBreakState ()
		{
			MainLoop nested = main_loop_stack.Peek ();
			nested.Completed = true;

			Report.Debug (DebugFlags.CLI, "{0} leave nested break state", nested);
		}

		void main_thread_main ()
		{
			while (true) {
				try {
					interpreter.ClearInterrupt ();
					DoRunMainLoop ();
					if (is_inferior_main)
						break;

					Console.WriteLine ();
				} catch (ST.ThreadAbortException) {
					ST.Thread.ResetAbort ();
				}
			}
		}

		protected void RunMainLoop ()
		{
			is_inferior_main = false;

			interrupt_thread.Start ();

			try {
				if (interpreter.Options.StartTarget)
					interpreter.Start ();

				main_thread.Start ();
				main_thread.Join ();
			} catch (ScriptingException ex) {
				interpreter.Error (ex);
			} catch (TargetException ex) {
				interpreter.Error (ex);
			} catch (Exception ex) {
				interpreter.Error ("ERROR: {0}", ex);
			} finally {
				interpreter.Exit ();
			}
		}

		public void RunInferiorMainLoop ()
		{
			is_inferior_main = true;

			interrupt_thread.Start ();

			TextReader old_stdin = Console.In;
			TextWriter old_stdout = Console.Out;
			TextWriter old_stderr = Console.Error;

			StreamReader stdin_reader = new StreamReader (Console.OpenStandardInput ());

			StreamWriter stdout_writer = new StreamWriter (Console.OpenStandardOutput ());
			stdout_writer.AutoFlush = true;

			StreamWriter stderr_writer = new StreamWriter (Console.OpenStandardError ());
			stderr_writer.AutoFlush = true;

			Console.SetIn (stdin_reader);
			Console.SetOut (stdout_writer);
			Console.SetError (stderr_writer);

			bool old_is_script = interpreter.IsScript;
			bool old_is_interactive = interpreter.IsInteractive;

			interpreter.IsScript = false;
			interpreter.IsInteractive = true;

			Console.WriteLine ();

			try {
				main_thread.Start ();
				main_thread.Join ();
			} catch (ScriptingException ex) {
				interpreter.Error (ex);
			} catch (TargetException ex) {
				interpreter.Error (ex);
			} catch (Exception ex) {
				interpreter.Error ("ERROR: {0}", ex);
			} finally {
				Console.WriteLine ();

				Console.SetIn (old_stdin);
				Console.SetOut (old_stdout);
				Console.SetError (old_stderr);
				interpreter.IsScript = old_is_script;
				interpreter.IsInteractive = old_is_interactive;
			}

			interrupt_thread.Abort ();
		}

		public string ReadInput (bool is_complete)
		{
			++line;
		again:
			string the_prompt = is_complete ? Prompt : "... ";
			if (interpreter.IsScript) {
				Console.Write (the_prompt);
				return Console.ReadLine ();
			} else {
				string result = GnuReadLine.ReadLine (the_prompt);
				if (result == null)
					return null;
				if (result != "")
					GnuReadLine.AddHistory (result);
				return result;
			}
		}

		public void Error (int pos, string message)
		{
			if (interpreter.Options.IsScript) {
				// If we're reading from a script, abort.
				Console.Write ("ERROR in line {0}, column {1}: ", line, pos);
				Console.WriteLine (message);
				Environment.Exit (1);
			}
			else {
				string prefix = new String (' ', pos + Prompt.Length);
				Console.WriteLine ("{0}^", prefix);
				Console.Write ("ERROR: ");
				Console.WriteLine (message);
			}
		}

		void interrupt_thread_main ()
		{
			do {
				Semaphore.Wait ();
				if (mono_debugger_server_get_pending_sigint () == 0)
					continue;

				if (interpreter.Interrupt () > 2)
					interrupt_event.Set ();
			} while (true);
		}

		public static void Main (string[] args)
		{
			bool is_terminal = GnuReadLine.IsTerminal (0);

			DebuggerOptions options = DebuggerOptions.ParseCommandLine (args);

			DebuggerConfiguration config = new DebuggerConfiguration ();
#if HAVE_XSP
			if (options.StartXSP)
				config.SetupXSP ();
			else
				config.LoadConfiguration ();
#else
			config.LoadConfiguration ();
#endif

			Console.WriteLine ("Mono Debugger");

			CommandLineInterpreter interpreter = new CommandLineInterpreter (
				is_terminal, config, options);

			interpreter.RunMainLoop ();

			config.SaveConfiguration ();
		}

		public class MainLoop
		{
			public Interpreter Interpreter { get; private set; }

			public ST.WaitHandle CompletedEvent {
				get { return completed_event; }
			}

			public bool Completed {
				get; set;
			}

			public int ID {
				get { return id; }
			}

			static int next_id;
			int id = ++next_id;

			ST.Thread command_thread;
			ST.ManualResetEvent command_event;
			ST.ManualResetEvent completed_event;
			bool repeating;
			Command last_command;
			Command command;

			public MainLoop (Interpreter interpreter)
			{
				this.Interpreter = interpreter;

				command_event = new ST.ManualResetEvent (false);
				completed_event = new ST.ManualResetEvent (false);

				command_thread = new ST.Thread (new ST.ThreadStart (command_thread_main));
				command_thread.Start ();
			}

			public void ExecuteCommand (Command command)
			{
				lock (this) {
					this.command = command;
					this.last_command = command;
					this.repeating = false;

					completed_event.Reset ();
					command_event.Set ();
				}
			}

			public bool Repeat ()
			{
				lock (this) {
					if (last_command == null)
						return false;

					this.command = last_command;
					this.repeating = true;

					completed_event.Reset ();
					command_event.Set ();
					return true;
				}
			}

			void command_thread_main ()
			{
				Report.Debug (DebugFlags.CLI, "{0} starting command thread", this);

				while (!Completed) {
					command_event.WaitOne ();

					Report.Debug (DebugFlags.CLI, "{0} command thread waiting", this);

					Command command;
					lock (this) {
						command = this.command;
						this.command = null;

						command_event.Reset ();
					}

					Report.Debug (DebugFlags.CLI, "{0} command thread execute: {1}", this, command);

					execute_command (command);

					Report.Debug (DebugFlags.CLI, "{0} command thread done executing: {1}", this, command);

					completed_event.Set ();
				}

				Report.Debug (DebugFlags.CLI, "{0} terminating command thread", this);
			}

			void execute_command (Command command)
			{
				try {
					if (repeating)
						command.Repeat (Interpreter);
					else
						command.Execute (Interpreter);
				} catch (ST.ThreadAbortException) {
				} catch (ScriptingException ex) {
					Interpreter.Error (ex);
				} catch (TargetException ex) {
					Interpreter.Error (ex);
				} catch (Exception ex) {
					Interpreter.Error (
						"Caught exception while executing command {0}: {1}",
						this, ex);
				}
			}

			public override string ToString ()
			{
				return String.Format ("MainLoop ({0}{1})", ID, Completed ? ":Completed" : "");
			}
		}
	}
}
