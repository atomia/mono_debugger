using GLib;
using Gtk;
using System;
using System.IO;
using System.Runtime.InteropServices;

namespace Mono.Debugger.GUI
{
	public class RegisterDisplay : DebuggerWidget
	{
		Gtk.TreeView tree;
		Gtk.ListStore store;

		public RegisterDisplay (IDebuggerBackend backend, Gtk.Container container)
			: base (backend, container)
		{
			store = new ListStore ((int)TypeFundamentals.TypeString,
					       (int)TypeFundamentals.TypeString);

			tree = new TreeView (store);

			tree.HeadersVisible = true;

			TreeViewColumn NameCol = new TreeViewColumn ();
			CellRenderer NameRenderer = new CellRendererText ();
			NameCol.Title = "Register";
			NameCol.PackStart (NameRenderer, true);
			NameCol.AddAttribute (NameRenderer, "text", 0);
			tree.AppendColumn (NameCol);

			TreeViewColumn DataCol = new TreeViewColumn ();
			CellRenderer DataRenderer = new CellRendererText ();
			DataCol.Title = "Value";
			DataCol.PackStart (DataRenderer, false);
			DataCol.AddAttribute (DataRenderer, "text", 1);
			tree.AppendColumn (DataCol);

			container.Add (tree);
			container.ShowAll ();

			backend.FrameChangedEvent += new StackFrameHandler (FrameChangedEvent);
			backend.FramesInvalidEvent += new StackFramesInvalidHandler (FramesInvalidEvent);
		}

		void FramesInvalidEvent ()
		{
			store.Clear ();
		}

		void FrameChangedEvent (IStackFrame frame)
		{
			store.Clear ();

			if (backend.Inferior == null)
				return;

			IArchitecture arch = backend.Inferior.Architecture;

			try {
				long[] regs = backend.Inferior.GetRegisters (arch.RegisterIndices);

				for (int i = 0; i < regs.Length; i++) {
					TreeIter iter = new TreeIter ();

					int idx = arch.RegisterIndices [i];
					GLib.Value Name = new GLib.Value (arch.RegisterNames [idx]);
					GLib.Value Value = new GLib.Value (arch.PrintRegister (idx, regs [i]));

					store.Append (out iter);
					store.SetValue (iter, 0, Name);
					store.SetValue (iter, 1, Value);
				}
			} catch {
				store.Clear ();
			}
		}

	}
}
