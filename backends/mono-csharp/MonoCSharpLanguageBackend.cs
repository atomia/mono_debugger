using System;
using System.IO;
using System.Text;
using System.Reflection;
using System.Collections;
using System.Threading;
using Mono.CSharp.Debugger;
using Mono.Debugger;
using Mono.Debugger.Backends;

namespace Mono.Debugger.Languages.CSharp
{
	internal delegate void BreakpointHandler (TargetAddress address, object user_data);

	internal class VariableInfo
	{
		public readonly int Index;
		public readonly int Offset;
		public readonly int Size;
		public readonly AddressMode Mode;
		public readonly int BeginScope;
		public readonly int EndScope;

		public enum AddressMode : long
		{
			Stack		= 0,
			Register	= 0x10000000,
			TwoRegisters	= 0x20000000
		}

		const long AddressModeFlags = 0xf0000000;

		public static int StructSize {
			get {
				return 20;
			}
		}

		// FIXME: Map mono/arch/x86/x86-codegen.h registers to
		//        debugger/arch/IArchitectureI386.cs registers.
		int[] register_map = { (int)I386Register.EAX, (int)I386Register.ECX,
				       (int)I386Register.EDX, (int)I386Register.EBX,
				       (int)I386Register.ESP, (int)I386Register.EBP,
				       (int)I386Register.ESI, (int)I386Register.EDI };

		public VariableInfo (TargetBinaryReader reader)
		{
			Index = reader.ReadInt32 ();
			Offset = reader.ReadInt32 ();
			Size = reader.ReadInt32 ();
			BeginScope = reader.ReadInt32 ();
			EndScope = reader.ReadInt32 ();

			Mode = (AddressMode) (Index & AddressModeFlags);
			Index = (int) ((long) Index & ~AddressModeFlags);

			if (Mode == AddressMode.Register)
				Index = register_map [Index];
		}

		public override string ToString ()
		{
			return String.Format ("[VariableInfo {0}:{1:x}:{2:x}:{3:x}:{4:x}:{5:x}]",
					      Mode, Index, Offset, Size, BeginScope, EndScope);
		}
	}

	internal struct JitLineNumberEntry
	{
		public readonly int Line;
		public readonly int Offset;
		public readonly int Address;

		public JitLineNumberEntry (TargetBinaryReader reader)
		{
			Line = reader.ReadInt32 ();
			Offset = reader.ReadInt32 ();
			Address = reader.ReadInt32 ();
		}
	}

	internal class MethodAddress
	{
		public readonly TargetAddress StartAddress;
		public readonly TargetAddress EndAddress;
		public readonly TargetAddress MethodStartAddress;
		public readonly TargetAddress MethodEndAddress;
		public readonly JitLineNumberEntry[] LineNumbers;
		public readonly VariableInfo ThisVariableInfo;
		public readonly VariableInfo[] ParamVariableInfo;
		public readonly VariableInfo[] LocalVariableInfo;
		public readonly TargetAddress ThisTypeInfoAddress;
		public readonly TargetAddress[] ParamTypeInfoAddresses;
		public readonly TargetAddress[] LocalTypeInfoAddresses;

		public MethodAddress (MethodEntry entry, TargetBinaryReader reader, object domain)
		{
			reader.Position = 4;
			StartAddress = new TargetAddress (domain, reader.ReadAddress ());
			EndAddress = new TargetAddress (domain, reader.ReadAddress ());
			MethodStartAddress = new TargetAddress (domain, reader.ReadAddress ());
			MethodEndAddress = new TargetAddress (domain, reader.ReadAddress ());

			int variables_offset = reader.ReadInt32 ();
			int type_table_offset = reader.ReadInt32 ();

			int num_line_numbers = reader.ReadInt32 ();
			LineNumbers = new JitLineNumberEntry [num_line_numbers];

			int line_number_offset = reader.ReadInt32 ();

			Report.Debug (DebugFlags.METHOD_ADDRESS,
				      "METHOD ADDRESS: {0} {1} {2} {3} {4} {5} {6}",
				      StartAddress, EndAddress, MethodStartAddress, MethodEndAddress,
				      variables_offset, type_table_offset, num_line_numbers);

			if (num_line_numbers > 0) {
				reader.Position = line_number_offset;
				for (int i = 0; i < num_line_numbers; i++)
					LineNumbers [i] = new JitLineNumberEntry (reader);
			}

			reader.Position = variables_offset;
			if (entry.ThisTypeIndex != 0)
				ThisVariableInfo = new VariableInfo (reader);

			ParamVariableInfo = new VariableInfo [entry.NumParameters];
			for (int i = 0; i < entry.NumParameters; i++)
				ParamVariableInfo [i] = new VariableInfo (reader);

			LocalVariableInfo = new VariableInfo [entry.NumLocals];
			for (int i = 0; i < entry.NumLocals; i++)
				LocalVariableInfo [i] = new VariableInfo (reader);

			reader.Position = type_table_offset;
			if (entry.ThisTypeIndex != 0)
				ThisTypeInfoAddress = new TargetAddress (domain, reader.ReadAddress ());

			ParamTypeInfoAddresses = new TargetAddress [entry.NumParameters];
			for (int i = 0; i < entry.NumParameters; i++)
				ParamTypeInfoAddresses [i] = new TargetAddress (domain, reader.ReadAddress ());

			LocalTypeInfoAddresses = new TargetAddress [entry.NumLocals];
			for (int i = 0; i < entry.NumLocals; i++)
				LocalTypeInfoAddresses [i] = new TargetAddress (domain, reader.ReadAddress ());
		}

		public override string ToString ()
		{
			return String.Format ("[Address {0:x}:{1:x}:{3:x}:{4:x},{2}]",
					      StartAddress, EndAddress, LineNumbers.Length,
					      MethodStartAddress, MethodEndAddress);
		}
	}

	// <summary>
	//   Holds all the symbol tables from the target's JIT.
	// </summary>
	internal class MonoSymbolFileTable
	{
		public const int  DynamicVersion = 16;
		public const long DynamicMagic   = 0x7aff65af4253d427;

		internal int TotalSize;
		internal int Generation;
		internal MonoSymbolTableReader[] SymbolFiles;
		public readonly MonoCSharpLanguageBackend Language;
		public readonly DebuggerBackend Backend;
		ArrayList ranges;
		Hashtable types;
		Hashtable type_cache;
		protected Hashtable modules;

		void child_exited ()
		{
			SymbolFiles = null;
			ranges = new ArrayList ();
			types = new Hashtable ();
			type_cache = new Hashtable ();
		}

		public MonoSymbolFileTable (DebuggerBackend backend, MonoCSharpLanguageBackend language)
		{
			this.Language = language;
			this.Backend = backend;

			modules = new Hashtable ();
		}

		// <summary>
		//   Read all symbol tables from the JIT.
		// </summary>
		internal void Reload (IInferior inferior, TargetAddress address)
		{
			lock (this) {
				Report.Debug (DebugFlags.JIT_SYMTAB, "SYMBOL FILE TABLE: {0}", address);

				ITargetMemoryReader header = inferior.ReadMemory (address, 24);

				Report.Debug (DebugFlags.JIT_SYMTAB, "SYMBOL FILE TABLE HEADER: {0}", header);

				long magic = header.ReadLongInteger ();
				if (magic != DynamicMagic)
					throw new SymbolTableException (
						"Dynamic section has unknown magic {0:x}.", magic);

				int version = header.ReadInteger ();
				if (version != DynamicVersion)
					throw new SymbolTableException (
						"Dynamic section has version {0}, but expected {1}.",
						version, DynamicVersion);

				ranges = new ArrayList ();
				types = new Hashtable ();
				type_cache = new Hashtable ();

				TotalSize = header.ReadInteger ();
				int count = header.ReadInteger ();
				Generation = header.ReadInteger ();

				Report.Debug (DebugFlags.JIT_SYMTAB, "SYMBOL FILE TABLE HEADER: {0} {1} {2}",
					      TotalSize, count, Generation);

				if ((TotalSize == 0) || (count == 0)) {
					SymbolFiles = new MonoSymbolTableReader [0];
					return;
				}

				ITargetMemoryReader reader = inferior.ReadMemory (address + 24, TotalSize - 24);

				Report.Debug (DebugFlags.JIT_SYMTAB, "SYMBOL FILE TABLE READER: {0}", reader);

				SymbolFiles = new MonoSymbolTableReader [count];
				for (int i = 0; i < count; i++)
					SymbolFiles [i] = new MonoSymbolTableReader (
						this, Backend, inferior, inferior, reader.ReadAddress (),
						Language);

				foreach (MonoSymbolTableReader symfile in SymbolFiles) {
					AssemblyName name = symfile.Assembly.GetName (true);
					MonoModule module = (MonoModule) modules [name.Name];
					if (module == null) {
						module = new MonoModule (this, name);
						modules.Add (name.Name, module);
					}
					symfile.Module = module;
					module.MonoSymbolTableReader = symfile;
				}

				foreach (MonoSymbolTableReader symfile in SymbolFiles) {
					MonoModule module = (MonoModule) symfile.Module;

					module.Assembly = symfile.Assembly;
					module.MonoSymbolTableReader = symfile;
					module.FileName = symfile.ImageFile;
				}

				foreach (MonoSymbolTableReader symfile in SymbolFiles)
					((MonoModule) symfile.Module).ReadReferences ();
			}
		}

		public MonoType GetType (Type type, int type_size, TargetAddress address)
		{
			throw new NotImplementedException ();
#if FALSE
			check_inferior ();
			if (type_cache.Contains (address.Address))
				return (MonoType) type_cache [address.Address];

			MonoType retval;
			if (!address.IsNull)
				retval = MonoType.GetType (type, memory, address, this);
			else
				retval = new MonoOpaqueType (type, type_size);

			type_cache.Add (address.Address, retval);
			return retval;
#endif
		}

		public MonoType GetTypeFromClass (long klass_address)
		{
			throw new NotImplementedException ();
#if FALSE
			check_inferior ();
			TypeEntry entry = (TypeEntry) types [klass_address];

			if (entry == null) {
				Console.WriteLine ("Can't find class at address {0:x}", klass_address);
				throw new InternalError ();
			}

			return MonoType.GetType (entry.Type, memory, entry.TypeInfo, this);
#endif
		}

		public ArrayList SymbolRanges {
			get {
				lock (this) {
					return ranges;
				}
			}
		}

		public ICollection Modules {
			get {
				lock (this) {
					return modules.Values;
				}
			}
		}

		internal void AddType (TypeEntry type)
		{
			lock (this) {
				if (!types.Contains (type.KlassAddress.Address))
					types.Add (type.KlassAddress.Address, type);
			}
		}

		public bool Update (ITargetMemoryAccess memory)
		{
			lock (this) {
				bool updated = false;
				for (int i = 0; i < SymbolFiles.Length; i++) {
					if (!SymbolFiles [i].Module.LoadSymbols)
						continue;

					if (SymbolFiles [i].Update (memory))
						updated = true;
				}

				if (!updated)
					return false;

				ranges = new ArrayList ();
				for (int i = 0; i < SymbolFiles.Length; i++) {
					if (!SymbolFiles [i].Module.LoadSymbols)
						continue;

					ranges.AddRange (SymbolFiles [i].SymbolRanges);
				}
				ranges.Sort ();

				return true;
			}
		}

		private class MonoModule : NativeModule
		{
			public string FileName;

			Assembly assembly;
			MonoSymbolFileTable table;
			MonoSymbolTableReader reader;

			public MonoModule (MonoSymbolFileTable table, AssemblyName name)
				: base (name.Name, table.Backend)
			{
				this.table = table;

				table.Backend.ModuleManager.AddModule (this);
			}

			public override ILanguageBackend Language {
				get {
					return table.Language;
				}
			}

			public override string FullName {
				get {
					if (FileName != null)
						return FileName;
					else
						return Name;
				}
			}

			public MonoSymbolTableReader MonoSymbolTableReader {
				get {
					return reader;
				}

				set {
					reader = value;
					if (reader != null)
						OnSymbolsLoadedEvent ();
					else
						OnSymbolsUnLoadedEvent ();
				}
			}

			public Assembly Assembly {
				get {
					return assembly;
				}

				set {
					assembly = value;
				}
			}

			public override bool SymbolsLoaded {
				get {
					return LoadSymbols && (reader != null) && (table != null);
				}
			}

			public override void UnLoad ()
			{
				reader = null;
				Assembly = null;
				base.UnLoad ();
			}

			protected override void SymbolsChanged (bool loaded)
			{
				// table.Update ();

				if (loaded)
					OnSymbolsLoadedEvent ();
				else
					OnSymbolsUnLoadedEvent ();
			}

			protected override SourceInfo[] GetSources ()
			{
				if (!SymbolsLoaded)
					return null;

				return reader.GetSources ();
			}

			public void ReadReferences ()
			{
				if ((table.modules == null) || (Assembly == null))
					return;

				AssemblyName[] references = Assembly.GetReferencedAssemblies ();
				foreach (AssemblyName name in references) {
					if (table.modules.Contains (name.Name))
						continue;

					MonoModule module = new MonoModule (table, name);
					table.modules.Add (name.Name, module);
				}
			}

			protected override void ReadModuleData ()
			{
				lock (this) {
					base.ReadModuleData ();
				}
			}

			public override SourceMethodInfo FindMethod (string name)
			{
				if (!SymbolsLoaded)
					return null;

				return reader.FindMethod (name);
			}

			protected override ISymbolTable GetSymbolTable ()
			{
				if (!SymbolsLoaded)
					return null;

				return reader.SymbolTable;
			}
		}
	}

	internal class MonoDebuggerInfo
	{
		public readonly TargetAddress generic_trampoline_code;
		public readonly TargetAddress breakpoint_trampoline_code;
		public readonly TargetAddress symbol_file_generation;
		public readonly TargetAddress symbol_file_modified;
		public readonly TargetAddress notification_code;
		public readonly TargetAddress symbol_file_table;
		public readonly TargetAddress compile_method;
		public readonly TargetAddress insert_breakpoint;
		public readonly TargetAddress remove_breakpoint;
		public readonly TargetAddress runtime_invoke;

		internal MonoDebuggerInfo (ITargetMemoryReader reader)
		{
			reader.Offset = reader.TargetLongIntegerSize +
				2 * reader.TargetIntegerSize;
			generic_trampoline_code = reader.ReadAddress ();
			breakpoint_trampoline_code = reader.ReadAddress ();
			symbol_file_generation = reader.ReadAddress ();
			symbol_file_modified = reader.ReadAddress ();
			notification_code = reader.ReadAddress ();
			symbol_file_table = reader.ReadAddress ();
			compile_method = reader.ReadAddress ();
			insert_breakpoint = reader.ReadAddress ();
			remove_breakpoint = reader.ReadAddress ();
			runtime_invoke = reader.ReadAddress ();
			Report.Debug (DebugFlags.JIT_SYMTAB, this);
		}

		public override string ToString ()
		{
			return String.Format (
				"MonoDebuggerInfo ({0:x}:{1:x}:{2:x}:{3:x}:{4:x}:{5:x}:{6:x}:{7:x}:{8:x})",
				generic_trampoline_code, breakpoint_trampoline_code,
				symbol_file_generation, symbol_file_modified, symbol_file_table,
				compile_method, insert_breakpoint, remove_breakpoint,
				runtime_invoke);
		}
	}

	internal class TypeEntry
	{
		public readonly TargetAddress KlassAddress;
		public readonly int Rank;
		public readonly int Token;
		public readonly TargetAddress TypeInfo;
		public readonly Type Type;

		static MethodInfo get_type;

		static TypeEntry ()
		{
			Type type = typeof (Assembly);
			get_type = type.GetMethod ("MonoDebugger_GetType");
			if (get_type == null)
				throw new InternalError (
					"Can't find Assembly.MonoDebugger_GetType");
		}

		private TypeEntry (MonoSymbolTableReader reader, ITargetMemoryReader memory)
		{
			KlassAddress = memory.ReadAddress ();
			Rank = memory.BinaryReader.ReadInt32 ();
			Token = memory.BinaryReader.ReadInt32 ();
			TypeInfo = memory.ReadAddress ();

			object[] args = new object[] { (int) Token };
			Type = (Type) get_type.Invoke (reader.Assembly, args);

			if (Type == null)
				Type = typeof (void);
			else if (Type == typeof (object))
				MonoType.GetType (Type, memory.TargetMemoryAccess, TypeInfo, reader.Table);
		}

		public static void ReadTypes (MonoSymbolTableReader reader,
					      ITargetMemoryReader memory, int count)
		{
			for (int i = 0; i < count; i++) {
				try {
					TypeEntry entry = new TypeEntry (reader, memory);
					reader.Table.AddType (entry);
				} catch (Exception e) {
					Console.WriteLine ("Can't read type: {0}", e);
					// Do nothing.
				}
			}
		}

		public override string ToString ()
		{
			return String.Format ("TypeEntry [{0:x}:{1:x}:{2:x}]",
					      KlassAddress, Token, TypeInfo);
		}
	}

	// <summary>
	//   A single Assembly's symbol table.
	// </summary>
	internal class MonoSymbolTableReader
	{
		MethodEntry[] Methods;
		internal readonly Assembly Assembly;
		internal readonly MonoSymbolFileTable Table;
		internal readonly string ImageFile;
		internal readonly string SymbolFile;
		internal Module Module;
		internal ThreadManager ThreadManager;
		internal ITargetInfo TargetInfo;
		protected OffsetTable offset_table;
		protected MonoCSharpLanguageBackend language;
		protected DebuggerBackend backend;
		protected Hashtable range_hash;
		MonoCSharpSymbolTable symtab;
		ArrayList ranges;

		TargetAddress dynamic_address;
		int address_size;
		int long_size;
		int int_size;

		int generation;
		int num_range_entries;
		int num_type_entries;

		TargetBinaryReader reader;
		TargetBinaryReader string_reader;

		internal MonoSymbolTableReader (MonoSymbolFileTable table, DebuggerBackend backend,
						ITargetInfo target_info, ITargetMemoryAccess memory,
						TargetAddress address, MonoCSharpLanguageBackend language)
		{
			this.Table = table;
			this.TargetInfo = target_info;
			this.backend = backend;
			this.language = language;

			ThreadManager = backend.ThreadManager;

			address_size = TargetInfo.TargetAddressSize;
			long_size = TargetInfo.TargetLongIntegerSize;
			int_size = TargetInfo.TargetIntegerSize;

			ranges = new ArrayList ();
			range_hash = new Hashtable ();

			long magic = memory.ReadLongInteger (address);
			if (magic != OffsetTable.Magic)
				throw new SymbolTableException (
					"Symbol file has unknown magic {0:x}.", magic);
			address += long_size;

			int version = memory.ReadInteger (address);
			if (version != OffsetTable.Version)
				throw new SymbolTableException (
					"Symbol file has version {0}, but expected {1}.",
					version, OffsetTable.Version);
			address += int_size;

			long dynamic_magic = memory.ReadLongInteger (address);
			if (dynamic_magic != MonoSymbolFileTable.DynamicMagic)
				throw new SymbolTableException (
					"Dynamic section has unknown magic {0:x}.", dynamic_magic);
			address += long_size;

			int dynamic_version = memory.ReadInteger (address);
			if (dynamic_version != MonoSymbolFileTable.DynamicVersion)
				throw new SymbolTableException (
					"Dynamic section has version {0}, but expected {1}.",
					dynamic_version, MonoSymbolFileTable.DynamicVersion);
			address += 2 * int_size;

			TargetAddress image_file_addr = memory.ReadAddress (address);
			address += address_size;
			ImageFile = memory.ReadString (image_file_addr);
			TargetAddress symbol_file_addr = memory.ReadAddress (address);
			address += address_size;
			SymbolFile = memory.ReadString (symbol_file_addr);
			TargetAddress raw_contents = memory.ReadAddress (address);
			address += address_size;
			int raw_contents_size = memory.ReadInteger (address);
			address += int_size;
			TargetAddress string_table_address = memory.ReadAddress (address);
			address += address_size;
			int string_table_size = memory.ReadInteger (address);
			address += int_size;

			dynamic_address = address;

			Assembly = Assembly.LoadFrom (ImageFile);

			if (raw_contents_size == 0)
				throw new SymbolTableException ("Symbol table is empty.");

			// This is a mmap()ed area and thus not written to the core file,
			// so we need to suck the whole file in.
			using (FileStream stream = File.OpenRead (SymbolFile)) {
				byte[] contents = new byte [raw_contents_size];
				stream.Read (contents, 0, raw_contents_size);
				reader = new TargetBinaryReader (contents, TargetInfo);
			}

			byte[] string_table = memory.ReadBuffer (string_table_address, string_table_size);
			string_reader = new TargetBinaryReader (string_table, TargetInfo);

			//
			// Read the offset table.
			//
			try {
				magic = reader.ReadInt64 ();
				version = reader.ReadInt32 ();
				if ((magic != OffsetTable.Magic) || (version != OffsetTable.Version))
					throw new SymbolTableException ();
				offset_table = new OffsetTable (reader);
			} catch {
				throw new SymbolTableException ();
			}

			symtab = new MonoCSharpSymbolTable (this);
		}

		public override string ToString ()
		{
			return String.Format ("{0} ({1}:{2})", GetType (), ImageFile, SymbolFile);
		}

		// <remarks>
		//   Each time we reload the JIT's symbol tables, add the addresses of all
		//   methods which have been JITed since the last update.
		// </remarks>
		bool update_ranges (ITargetMemoryAccess memory, ref TargetAddress address)
		{
			TargetAddress range_table = memory.ReadAddress (address);
			address += address_size;
			int range_entry_size = memory.ReadInteger (address);
			address += int_size;
			int new_num_range_entries = memory.ReadInteger (address);
			address += int_size;

			if (new_num_range_entries == num_range_entries)
				return false;

			int count = new_num_range_entries - num_range_entries;
			ITargetMemoryReader range_reader = memory.ReadMemory (
				range_table + num_range_entries * range_entry_size,
				count * range_entry_size);

			ArrayList new_ranges = MethodRangeEntry.ReadRanges (
				this, range_reader, count, offset_table);

			ranges.AddRange (new_ranges);
			num_range_entries = new_num_range_entries;
			return true;
		}

		// <summary>
		//   Add all types which have been created in the meantime.
		// </summary>
		bool update_types (ITargetMemoryAccess memory, ref TargetAddress address)
		{
			TargetAddress type_table = memory.ReadAddress (address);
			address += address_size;
			int type_entry_size = memory.ReadInteger (address);
			address += int_size;
			int new_num_type_entries = memory.ReadInteger (address);
			address += int_size;

			if (new_num_type_entries == num_type_entries)
				return false;

			int count = new_num_type_entries - num_type_entries;
			ITargetMemoryReader type_reader = memory.ReadMemory (
				type_table + num_type_entries * type_entry_size,
				count * type_entry_size);

			TypeEntry.ReadTypes (this, type_reader, count);

			num_type_entries = new_num_type_entries;
			return true;
		}

		public bool Update (ITargetMemoryAccess memory)
		{
			TargetAddress address = dynamic_address;
			if (memory.ReadInteger (address) != 0)
				return false;
			address += int_size;

			int new_generation = memory.ReadInteger (address);
			if (new_generation == generation)
				return false;
			address += int_size;

			generation = new_generation;

			bool updated = false;

			updated |= update_ranges (memory, ref address);
			updated |= update_types (memory, ref address);

			return true;
		}

		internal int CheckMethodOffset (long offset)
		{
			if (offset < offset_table.method_table_offset)
				throw new SymbolTableException ();

			offset -= offset_table.method_table_offset;
			if ((offset % MethodEntry.Size) != 0)
				throw new SymbolTableException ();

			long index = (offset / MethodEntry.Size);
			if (index > offset_table.method_count)
				throw new SymbolTableException ();

			return (int) index;
		}

		internal int GetMethodOffset (int index)
		{
			return offset_table.method_table_offset + index * MethodEntry.Size;
		}

		Hashtable method_hash;

		protected MonoMethod GetMethod (long offset)
		{
			int index = CheckMethodOffset (offset);
			reader.Position = offset;
			MethodEntry method = new MethodEntry (reader);
			string_reader.Position = index * int_size;
			string_reader.Position = string_reader.ReadInt32 ();

			int length = string_reader.ReadInt32 ();
			byte[] buffer = string_reader.ReadBuffer (length);
			string name = Encoding.UTF8.GetString (buffer);

			MonoMethod mono_method = new MonoMethod (this, method, name);
			if (method_hash == null)
				method_hash = new Hashtable ();
			method_hash.Add (offset, mono_method);
			return mono_method;
		}

		protected MonoMethod GetMethod (long offset, byte[] contents)
		{
			MonoMethod method = null;
			if (method_hash != null)
				method = (MonoMethod) method_hash [offset];

			if (method == null) {
				int index = CheckMethodOffset (offset);
				reader.Position = offset;
				MethodEntry entry = new MethodEntry (reader);
				string_reader.Position = index * int_size;
				string_reader.Position = string_reader.ReadInt32 ();

				int length = string_reader.ReadInt32 ();
				byte[] buffer = string_reader.ReadBuffer (length);
				string name = Encoding.UTF8.GetString (buffer);

				method = new MonoMethod (this, entry, name);
			}

			if (!method.IsLoaded) {
				TargetBinaryReader reader = new TargetBinaryReader (contents, TargetInfo);
				method.Load (reader, (object) ThreadManager);
			}

			return method;
		}

		ArrayList sources = null;
		Hashtable method_name_hash = null;
		void ensure_sources ()
		{
			if (sources != null)
				return;

			sources = new ArrayList ();
			method_name_hash = new Hashtable ();

			reader.Position = offset_table.source_file_table_offset;
			for (int i = 0; i < offset_table.source_file_count; i++) {
				int offset = (int) reader.Position;

				SourceFileEntry source = new SourceFileEntry (reader);
				MonoSourceInfo info = new MonoSourceInfo (this, source);

				ArrayList methods = new ArrayList ();
				foreach (MethodSourceEntry entry in source.Methods) {
					string_reader.Position = entry.Index * int_size;
					string_reader.Position = string_reader.ReadInt32 ();

					int length = string_reader.ReadInt32 ();
					byte[] buffer = string_reader.ReadBuffer (length);
					string name = Encoding.UTF8.GetString (buffer);

					if (method_name_hash.Contains (name)) {
						Console.WriteLine (
							"Already have a method with this name: {1}", name);
						continue;
					}

					MonoMethodSourceEntry method = new MonoMethodSourceEntry (
						this, entry, info, name);

					method_name_hash.Add (name, method);
					methods.Add (method);
				}
				info.methods = methods;

				sources.Add (info);
			}
		}

		public SourceInfo[] GetSources ()
		{
			ensure_sources ();
			SourceInfo[] retval = new SourceInfo [sources.Count];
			sources.CopyTo (retval, 0);
			return retval;
		}

		public SourceMethodInfo FindMethod (string name)
		{
			ensure_sources ();
			MonoMethodSourceEntry method = (MonoMethodSourceEntry) method_name_hash [name];
			if (method == null)
				return null;

			return method.Method;
		}

		internal ArrayList SymbolRanges {
			get {
				return ranges;
			}
		}

		internal ISymbolTable SymbolTable {
			get {
				return symtab;
			}
		}

		// <remarks>
		//   Wrapper around MethodSourceEntry; holds a reference to the
		//   MonoSourceMethod while the method is loaded.  We only create the
		//   MonoSourceMethod when the method is actually used since it consumes a
		//   lot of memory and also takes some time to create it.
		// </remarks>
		private class MonoMethodSourceEntry
		{
			// <summary>
			//   This is read from the symbol file.
			// </summary>
			public readonly MethodSourceEntry Entry;
			public readonly MonoSourceInfo SourceInfo;

			// <summary>
			//   The method name is read from the JIT.
			// </summary>
			public readonly string Name;

			public MonoMethodSourceEntry (MonoSymbolTableReader reader, MethodSourceEntry entry,
						      MonoSourceInfo source, string name)
			{
				this.reader = reader;
				this.Entry = entry;
				this.SourceInfo = source;
				this.Name = name;
			}

			MonoSymbolTableReader reader;
			MonoSourceMethod method = null;

			public MonoSourceMethod Method {
				get {
					if (method != null)
						return method;

					method = new MonoSourceMethod (SourceInfo, reader, Entry, Name);
					return method;
				}
			}

		}

		private class MonoSourceInfo : SourceInfo
		{
			MonoSymbolTableReader reader;
			SourceFileEntry source;
			public ArrayList methods;

			public MonoSourceInfo (MonoSymbolTableReader reader, SourceFileEntry source)
				: base (reader.Module, source.SourceFile)
			{
				this.reader = reader;
				this.source = source;
			}

			protected override ArrayList GetMethods ()
			{
				ArrayList list = new ArrayList ();
				if (methods == null)
					return list;

				foreach (MonoMethodSourceEntry method in methods)
					list.Add (method.Method);
				return list;
			}
		}

		private class MonoSourceMethod : SourceMethodInfo
		{
			MonoSymbolTableReader reader;
			Hashtable load_handlers;
			int offset;
			string full_name;
			MonoMethod method;
			MethodSourceEntry entry;

			public MonoSourceMethod (SourceInfo source, MonoSymbolTableReader reader,
						 MethodSourceEntry entry, string name)
				: base (source, name, entry.StartRow, entry.EndRow, true)
			{
				this.reader = reader;
				this.offset = reader.GetMethodOffset (entry.Index);
				this.entry = entry;

				source.Module.ModuleUnLoadedEvent += new ModuleEventHandler (module_unloaded);
			}

			void module_unloaded (Module module)
			{
				reader = null;
				method = null;
			}

			public override bool IsLoaded {
				get {
					return (method != null) &&
						((reader != null) && reader.range_hash.Contains (offset));
				}
			}

			void ensure_method ()
			{
				if ((method != null) && method.IsLoaded)
					return;

				MethodRangeEntry entry = (MethodRangeEntry) reader.range_hash [offset];
				method = entry.GetMethod ();
			}

			public override IMethod Method {
				get {
					if (!IsLoaded)
						throw new InvalidOperationException ();

					ensure_method ();
					return method;
				}
			}

			public override TargetAddress Lookup (int line)
			{
				if (!IsLoaded)
					throw new InvalidOperationException ();

				ensure_method ();
				if (method.HasSource)
					return method.Source.Lookup (line);
				else
					return TargetAddress.Null;
			}

			void breakpoint_hit (TargetAddress address, object user_data)
			{
				if (load_handlers == null)
					return;

				ensure_method ();

				foreach (HandlerData handler in load_handlers.Keys)
					handler.Handler (handler.Method, handler.UserData);

				load_handlers = null;
			}

			public override IDisposable RegisterLoadHandler (MethodLoadedHandler handler,
									 object user_data)
			{
				HandlerData data = new HandlerData (this, handler, user_data);

				if (load_handlers == null) {
					load_handlers = new Hashtable ();

					if (method == null)
						method = reader.GetMethod (offset);
					MethodInfo minfo = (MethodInfo) method.MethodHandle;

					string full_name = String.Format (
						"{0}:{1}", minfo.ReflectedType.FullName, minfo.Name);

					reader.Table.Language.InsertBreakpoint (
						full_name, new BreakpointHandler (breakpoint_hit), null);
				}

				load_handlers.Add (data, true);
				return data;
			}

			protected void UnRegisterLoadHandler (HandlerData data)
			{
				if (load_handlers == null)
					return;

				load_handlers.Remove (data);
				if (load_handlers.Count == 0)
					load_handlers = null;
			}

			private sealed class HandlerData : IDisposable
			{
				public readonly MonoSourceMethod Method;
				public readonly MethodLoadedHandler Handler;
				public readonly object UserData;

				public HandlerData (MonoSourceMethod method, MethodLoadedHandler handler,
						    object user_data)
				{
					this.Method = method;
					this.Handler = handler;
					this.UserData = user_data;
				}

				private bool disposed = false;

				private void Dispose (bool disposing)
				{
					if (!this.disposed) {
						if (disposing) {
							Method.UnRegisterLoadHandler (this);
						}
					}
						
					this.disposed = true;
				}

				public void Dispose ()
				{
					Dispose (true);
					// Take yourself off the Finalization queue
					GC.SuppressFinalize (this);
				}

				~HandlerData ()
				{
					Dispose (false);
				}
			}
		}

		protected class MonoMethod : MethodBase
		{
			MonoSymbolTableReader reader;
			MethodEntry method;
			System.Reflection.MethodBase rmethod;
			MonoType this_type;
			MonoType[] param_types;
			MonoType[] local_types;
			IVariable[] parameters;
			IVariable[] locals;
			bool has_variables;
			bool is_loaded;
			MethodAddress address;

			static MethodInfo get_method;
			static MethodInfo get_local_type_from_sig;

			static MonoMethod ()
			{
				Type type = typeof (Assembly);
				get_method = type.GetMethod ("MonoDebugger_GetMethod");
				if (get_method == null)
					throw new InternalError (
						"Can't find Assembly.MonoDebugger_GetMethod");
				get_local_type_from_sig = type.GetMethod ("MonoDebugger_GetLocalTypeFromSignature");
				if (get_local_type_from_sig == null)
					throw new InternalError (
						"Can't find Assembly.MonoDebugger_GetLocalTypeFromSignature");

			}

			public MonoMethod (MonoSymbolTableReader reader, MethodEntry method, string name)
				: base (name, reader.ImageFile, reader.Module)
			{
				this.reader = reader;
				this.method = method;

				object[] args = new object[] { (int) method.Token };
				rmethod = (System.Reflection.MethodBase) get_method.Invoke (
					reader.Assembly, args);
			}

			public MonoMethod (MonoSymbolTableReader reader, MethodEntry method,
					   string name, ITargetMemoryReader dynamic_reader)
				: this (reader, method, name)
			{
				Load (dynamic_reader.BinaryReader, (object) reader.ThreadManager);
			}

			public void Load (TargetBinaryReader dynamic_reader, object domain)
			{
				if (is_loaded)
					throw new InternalError ();

				is_loaded = true;

				address = new MethodAddress (method, dynamic_reader, domain);

				SetAddresses (address.StartAddress, address.EndAddress);
				SetMethodBounds (address.MethodStartAddress, address.MethodEndAddress);

				IMethodSource source = CSharpMethod.GetMethodSource (
					this, method, address.LineNumbers);

				if (source != null)
					SetSource (source);
			}

			void get_variables ()
			{
				if (has_variables || !is_loaded)
					return;

				if (!address.ThisTypeInfoAddress.IsNull)
					this_type = reader.Table.GetType (
						rmethod.ReflectedType, 0, address.ThisTypeInfoAddress);

				ParameterInfo[] param_info = rmethod.GetParameters ();
				param_types = new MonoType [param_info.Length];
				for (int i = 0; i < param_info.Length; i++)
					param_types [i] = reader.Table.GetType (
						param_info [i].ParameterType,
						address.ParamVariableInfo [i].Size,
						address.ParamTypeInfoAddresses [i]);

				parameters = new IVariable [param_info.Length];
				for (int i = 0; i < param_info.Length; i++)
					parameters [i] = new MonoVariable (
						reader.backend, param_info [i].Name, param_types [i],
						false, this, address.ParamVariableInfo [i]);

				local_types = new MonoType [method.NumLocals];
				for (int i = 0; i < method.NumLocals; i++) {
					LocalVariableEntry local = method.Locals [i];

					object[] args = new object[] { local.Signature };
					Type type = (Type) get_local_type_from_sig.Invoke (
						reader.Assembly, args);

					local_types [i] = reader.Table.GetType (
						type, address.LocalVariableInfo [i].Size,
						address.LocalTypeInfoAddresses [i]);
				}

				locals = new IVariable [method.NumLocals];
				for (int i = 0; i < method.NumLocals; i++) {
					LocalVariableEntry local = method.Locals [i];

					locals [i] = new MonoVariable (
						reader.backend, local.Name, local_types [i],
						true, this, address.LocalVariableInfo [i]);
				}

				has_variables = true;
			}

			public override object MethodHandle {
				get {
					return rmethod;
				}
			}

			public override IVariable[] Parameters {
				get {
					if (!is_loaded)
						throw new InvalidOperationException ();

					get_variables ();
					return parameters;
				}
			}

			public override IVariable[] Locals {
				get {
					if (!is_loaded)
						throw new InvalidOperationException ();

					get_variables ();
					return locals;
				}
			}
		}

		private class MethodRangeEntry : SymbolRangeEntry
		{
			MonoSymbolTableReader reader;
			int file_offset;
			byte[] contents;

			private MethodRangeEntry (MonoSymbolTableReader reader, int file_offset,
						  byte[] contents, TargetAddress start_address,
						  TargetAddress end_address)
				: base (start_address, end_address)
			{
				this.reader = reader;
				this.file_offset = file_offset;
				this.contents = contents;
			}

			public static ArrayList ReadRanges (MonoSymbolTableReader reader,
							    ITargetMemoryReader memory, int count,
							    OffsetTable offset_table)
			{
				ArrayList list = new ArrayList ();

				for (int i = 0; i < count; i++) {
					TargetAddress start = memory.ReadGlobalAddress ();
					TargetAddress end = memory.ReadGlobalAddress ();
					int offset = memory.ReadInteger ();
					TargetAddress dynamic_address = memory.ReadAddress ();
					int dynamic_size = memory.ReadInteger ();

					byte[] contents = memory.TargetMemoryAccess.ReadBuffer (
						dynamic_address, dynamic_size);

					reader.CheckMethodOffset (offset);

					MethodRangeEntry entry = new MethodRangeEntry (
						reader, offset, contents, start, end);

					list.Add (entry);
					reader.range_hash.Add (offset, entry);
				}

				return list;
			}

			internal MonoMethod GetMethod ()
			{
				return reader.GetMethod (file_offset, contents);
			}

			protected override ISymbolLookup GetSymbolLookup ()
			{
				return reader.GetMethod (file_offset, contents);
			}

			public override string ToString ()
			{
				return String.Format ("RangeEntry [{0:x}:{1:x}:{2:x}]",
						      StartAddress, EndAddress, file_offset);
			}
		}

		private class MonoCSharpSymbolTable : SymbolTable
		{
			MonoSymbolTableReader reader;

			public MonoCSharpSymbolTable (MonoSymbolTableReader reader)
			{
				this.reader = reader;
			}

			public override bool HasMethods {
				get {
					return false;
				}
			}

			protected override ArrayList GetMethods ()
			{
				throw new InvalidOperationException ();
			}

			public override bool HasRanges {
				get {
					return true;
				}
			}

			public override ISymbolRange[] SymbolRanges {
				get {
					ArrayList ranges = reader.SymbolRanges;
					ISymbolRange[] retval = new ISymbolRange [ranges.Count];
					ranges.CopyTo (retval, 0);
					return retval;
				}
			}

			public override void UpdateSymbolTable ()
			{
				base.UpdateSymbolTable ();
			}
		}
	}

	internal class MonoCSharpLanguageBackend : ILanguageBackend
	{
		Process process;
		DebuggerBackend backend;
		MonoDebuggerInfo info;
		int symtab_generation;
		TargetAddress trampoline_address;
		TargetAddress notification_address;
		bool initialized;
		ManualResetEvent reload_event;
		protected MonoSymbolFileTable table;

		public MonoCSharpLanguageBackend (DebuggerBackend backend)
		{
			this.backend = backend;
			reload_event = new ManualResetEvent (false);
		}

		public string Name {
			get {
				return "Mono";
			}
		}

		public Process Process {
			get {
				return process;
			}

			set {
				process = value;
				if (process != null)
					init_process ();
				else
					child_exited ();
			}
		}

		internal MonoDebuggerInfo MonoDebuggerInfo {
			get {
				return info;
			}
		}

		void init_process ()
		{
			// sse = process.SingleSteppingEngine;
			breakpoints = new Hashtable ();
			process.TargetExited += new TargetExitedHandler (child_exited);
		}

		void child_exited ()
		{
			process = null;
			info = null;
			symtab_generation = 0;
			trampoline_address = TargetAddress.Null;
		}

		public Module[] Modules {
			get {
				if (table == null)
					return new Module [0];

				ICollection modules = table.Modules;
				if (modules == null)
					return new Module [0];

				Module[] retval = new Module [modules.Count];
				modules.CopyTo (retval, 0);
				return retval;
			}
		}

		void read_mono_debugger_info (IInferior inferior)
		{
			TargetAddress symbol_info = inferior.SimpleLookup ("MONO_DEBUGGER__debugger_info");
			if (symbol_info.IsNull)
				throw new SymbolTableException (
					"Can't get address of `MONO_DEBUGGER__debugger_info'.");

			ITargetMemoryReader header = inferior.ReadMemory (symbol_info, 16);
			long magic = header.ReadLongInteger ();
			if (magic != MonoSymbolFileTable.DynamicMagic)
				throw new SymbolTableException (
					"`MONO_DEBUGGER__debugger_info' has unknown magic {0:x}.", magic);

			int version = header.ReadInteger ();
			if (version != MonoSymbolFileTable.DynamicVersion)
				throw new SymbolTableException (
					"`MONO_DEBUGGER__debugger_info' has version {0}, but expected {1}.",
					version, MonoSymbolFileTable.DynamicVersion);

			int size = (int) header.ReadInteger ();

			ITargetMemoryReader table = inferior.ReadMemory (symbol_info, size);
			info = new MonoDebuggerInfo (table);

			trampoline_address = inferior.ReadGlobalAddress (info.generic_trampoline_code);

			notification_address = inferior.ReadGlobalAddress (info.notification_code);
			Console.WriteLine ("NOTIFICATION ADDRESS: {0} {1}", info.notification_code,
					   notification_address);
		}

		public void do_update_symbol_table (IInferior inferior)
		{
			try {
				int modified = inferior.ReadInteger (info.symbol_file_modified);
				if (modified == 0)
					return;

				int generation = inferior.ReadInteger (info.symbol_file_generation);
				if ((table != null) && (generation == symtab_generation)) {
					table.Update (inferior);
					return;
				}
			} catch (Exception e) {
				Console.WriteLine ("Can't update symbol table: {0}", e);
				table = null;
				return;
			}

			try {
				do_update_symbol_files (inferior);
			} catch (Exception e) {
				Console.WriteLine ("Can't update symbol table: {0}", e);
				table = null;
			}
		}

		void do_update_symbol_files (IInferior inferior)
		{
			Console.WriteLine ("Re-reading symbol files.");

			TargetAddress address = inferior.ReadAddress (info.symbol_file_table);
			if (address.IsNull) {
				Console.WriteLine ("Ooops, no symtab loaded.");
				return;
			}

			bool must_update = false;
			if (table == null) {
				table = new MonoSymbolFileTable (backend, this);
				must_update = true;
			}
			table.Reload (inferior, address);

			symtab_generation = table.Generation;

			table.Update (inferior);

			Console.WriteLine ("Done re-reading symbol files.");
		}

		Hashtable breakpoints = new Hashtable ();

		internal int InsertBreakpoint (string method_name, BreakpointHandler handler,
					       object user_data)
		{
#if FALSE
			long retval = sse.CallMethod (info.insert_breakpoint, 0, method_name);
			int index = (int) retval;

			if (index <= 0)
				return -1;

			breakpoints.Add (index, new BreakpointHandle (index, handler, user_data));
			return index;
#else
			throw new NotImplementedException ();
#endif
		}

		private struct BreakpointHandle
		{
			public readonly int Index;
			public readonly BreakpointHandler Handler;
			public readonly object UserData;

			public BreakpointHandle (int index, BreakpointHandler handler, object user_data)
			{
				this.Index = index;
				this.Handler = handler;
				this.UserData = user_data;
			}
		}

		public TargetAddress GenericTrampolineCode {
			get {
				return trampoline_address;
			}
		}

		public TargetAddress GetTrampoline (IProcess iprocess, TargetAddress address)
		{
			Process process = (Process) iprocess;
			IInferior inferior = process.Inferior;
			IArchitecture arch = process.Architecture;

			ThreadManager thread_manager = process.DebuggerBackend.ThreadManager;

			if (trampoline_address.IsNull)
				return TargetAddress.Null;

			TargetAddress trampoline = arch.GetTrampoline (address, trampoline_address);

			if (trampoline.IsNull)
				return TargetAddress.Null;

			long result;
			lock (this) {
				reload_event.Reset ();
				result = inferior.CallMethod (info.compile_method, trampoline.Address);
			}
			reload_event.WaitOne ();

			TargetAddress method;
			switch (inferior.TargetAddressSize) {
			case 4:
				method = new TargetAddress (thread_manager, (int) result);
				break;

			case 8:
				method = new TargetAddress (thread_manager, result);
				break;
				
			default:
				throw new TargetMemoryException (
					"Unknown target address size " + inferior.TargetAddressSize);
			}

			return method;
		}

		public bool BreakpointHit (IProcess iprocess, TargetAddress address)
		{
			Process process = (Process) iprocess;
			IInferior inferior = process.Inferior;
			IArchitecture arch = process.Architecture;

			if ((info == null) || (inferior == null))
				return true;

			try {
				TargetAddress trampoline = inferior.ReadAddress (
					info.breakpoint_trampoline_code);
				if (trampoline.IsNull || (inferior.CurrentFrame != trampoline + 1))
					return true;

				TargetAddress method, code, retaddr;
				int breakpoint_id = arch.GetBreakpointTrampolineData (
					out method, out code, out retaddr);

				if (!breakpoints.Contains (breakpoint_id))
					return false;

				Console.WriteLine ("TRAMPOLINE BREAKPOINT: {0} {1} {2} {3} {4}",
						   code, method, breakpoint_id, retaddr,
						   breakpoints.Contains (breakpoint_id));

				BreakpointHandle handle = (BreakpointHandle) breakpoints [breakpoint_id];
				handle.Handler (code, handle.UserData);
				breakpoints.Remove (breakpoint_id);

				return false;
			} catch (Exception e) {
				Console.WriteLine ("BREAKPOINT EXCEPTION: {0}", e);
				// Do nothing.
			}
			return true;
		}

		public bool DaemonThreadHandler (DaemonThreadRunner runner, TargetAddress address, int signal)
		{
			if (!initialized) {
				read_mono_debugger_info (runner.Inferior);
				initialized = true;
			}

			if (signal == runner.Inferior.StopSignal) {
				runner.Inferior.SetSignal (0, false);
				return true;
			}

			if ((signal != 0) || (address != notification_address))
				return false;

			lock (this) {
				do_update_symbol_table (runner.Inferior);
				reload_event.Set ();
			}

			return true;
		}
	}
}
