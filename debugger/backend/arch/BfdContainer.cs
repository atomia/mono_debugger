using System;
using System.IO;
using System.Text;
using System.Collections;
using System.Runtime.InteropServices;

using Mono.Debugger.Languages;
using Mono.Debugger.Languages.Native;

namespace Mono.Debugger.Backend
{
	internal class BfdContainer : DebuggerMarshalByRefObject, IDisposable
	{
		Hashtable bfd_hash;
		Hashtable type_hash;
		ProcessServant process;
		NativeLanguage language;
		Bfd main_bfd;

		public BfdContainer (ProcessServant process)
		{
			this.process = process;
			this.bfd_hash = Hashtable.Synchronized (new Hashtable ());
			this.type_hash = Hashtable.Synchronized (new Hashtable ());
		}

		public NativeLanguage NativeLanguage {
			get { return language; }
		}

		public ProcessServant Process {
			get { return process; }
		}

		public Architecture Architecture {
			get { return process.Architecture; }
		}

		public Bfd this [string filename] {
			get {
				check_disposed ();
				return (Bfd) bfd_hash [filename];
			}
		}

		public Bfd LookupLibrary (TargetAddress address)
		{
			foreach (Bfd bfd in bfd_hash.Values) {
				if (!bfd.IsContinuous)
					continue;

				if ((address >= bfd.StartAddress) && (address < bfd.EndAddress))
					return bfd;
			}

			return null;
		}

		internal void SetupInferior (TargetInfo info, Bfd main_bfd)
		{
			this.main_bfd = main_bfd;
			language = new NativeLanguage (this, info);
		}

		public Bfd AddFile (TargetMemoryInfo memory, string filename,
				    TargetAddress base_address, bool step_info, bool is_loaded)
		{
			check_disposed ();
			Bfd bfd = (Bfd) bfd_hash [filename];
			if (bfd != null)
				return bfd;

			bfd = new Bfd (this, memory, filename, main_bfd, base_address, is_loaded);
			bfd_hash.Add (filename, bfd);
			return bfd;
		}

		public TargetAddress LookupSymbol (string name)
		{
			foreach (Bfd bfd in bfd_hash.Values) {
				TargetAddress symbol = bfd [name];
				if (!symbol.IsNull)
					return symbol;
			}

			return TargetAddress.Null;
		}

		public TargetType LookupType (string name)
		{
			foreach (Bfd bfd in bfd_hash.Values)
				bfd.ReadTypes ();

			ITypeEntry entry = (ITypeEntry) type_hash [name];
			if (entry == null)
				return null;

			return entry.ResolveType ();
		}

		public void AddType (ITypeEntry entry)
		{
			if (!type_hash.Contains (entry.Name))
				type_hash.Add (entry.Name, entry);

			if (entry.IsComplete)
				type_hash [entry.Name] = entry;
		}

		public void CloseBfd (Bfd bfd)
		{
			if (bfd == null)
				return;

			bfd_hash.Remove (bfd.FileName);
			bfd.Dispose ();
		}

		public Bfd FindLibrary (string name)
		{
			foreach (Bfd bfd in bfd_hash.Values) {
				if (Path.GetFileName (bfd.FileName) == name)
					return bfd;
			}

			return null;
		}

		public bool GetTrampoline (TargetMemoryAccess memory, TargetAddress address,
					   out TargetAddress trampoline, out bool is_start)
		{
			foreach (Bfd bfd in bfd_hash.Values) {
				if (bfd.GetTrampoline (memory, address, out trampoline, out is_start))
					return true;
			}

			is_start = false;
			trampoline = TargetAddress.Null;
			return false;
		}

		//
		// IDisposable
		//

		private bool disposed = false;

		private void check_disposed ()
		{
			if (disposed)
				throw new ObjectDisposedException ("BfdContainer");
		}

		protected virtual void Dispose (bool disposing)
		{
			if (!this.disposed) {
				if (disposing) {
					if (bfd_hash != null) {
						foreach (Bfd bfd in bfd_hash.Values)
							bfd.Dispose ();
						bfd_hash = null;
					}
				}
				
				this.disposed = true;
			}
		}

		public void Dispose ()
		{
			Dispose (true);
			// Take yourself off the Finalization queue
			GC.SuppressFinalize (this);
		}

		~BfdContainer ()
		{
			Dispose (false);
		}

	}
}