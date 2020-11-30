﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using MonoMod.Utils;

namespace BepInEx.IL2CPP.Allocator
{
	/// <summary>
	///     Based on https://github.com/kubo/funchook
	/// </summary>
	internal abstract class UnixPageAllocator : PageAllocator
	{
		protected abstract IMemoryMapper OpenMemoryMap();

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static bool CheckFreeRegionBefore(IntPtr start, IntPtr hint, IntPtr[] result)
		{
			if (start.ToInt64() < hint.ToInt64())
			{
				var addr = start - PAGE_SIZE;
				if (hint.ToInt64() - addr.ToInt64() < int.MaxValue)
					result[0] = addr;
			}

			return false;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private static bool CheckFreeRegionAfter(IntPtr end, IntPtr hint, IntPtr[] result)
		{
			if (hint.ToInt64() < end.ToInt64())
			{
				if (end.ToInt64() - hint.ToInt64() < int.MaxValue)
					result[1] = end;
				return true;
			}

			return false;
		}

		private IntPtr[] GetFreeAddresses(IntPtr hint)
		{
			var result = new IntPtr[2];
			var prevEnd = IntPtr.Zero;
			using var mapper = OpenMemoryMap();

			while (mapper.FindNextFree(out var start, out var end))
			{
				if ((prevEnd + PAGE_SIZE).ToInt64() <= start.ToInt64())
					if (CheckFreeRegionBefore(start, hint, result) || CheckFreeRegionAfter(prevEnd, hint, result))
						return result;
				prevEnd = end;
			}

			if (CheckFreeRegionAfter(prevEnd, hint, result))
				return result;
			throw new PageAllocatorException($"Could not find free region near {hint.ToInt64():X8}");
		}

		protected override IntPtr AllocateChunk(IntPtr hint)
		{
			/* From https://github.com/kubo/funchook/blob/master/src/funchook_unix.c#L251-L254:
			 * Loop three times just to avoid rare cases such as
			 * unused memory region is used between 'get_free_address()'
			 * and 'mmap()'.
			*/
			const int retryCount = 3;

			for (var attempt = 0; attempt < retryCount; attempt++)
			{
				var freeAdrresses = GetFreeAddresses(hint);
				// Try to use addr[1] (allocated after original method) first, then try before
				for (int i = freeAdrresses.Length - 1; i >= 0; i--)
				{
					var addr = freeAdrresses[i];
					if (addr == IntPtr.Zero)
						continue;
					var result = Unix.mmap(freeAdrresses[i], (UIntPtr)PAGE_SIZE, Unix.Protection.PROT_READ | Unix.Protection.PROT_WRITE, Unix.MapFlags.MAP_PRIVATE | Unix.MapFlags.MAP_ANONYMOUS, -1, 0);
					if (result == freeAdrresses[i])
						return result;
					if (result == Unix.MAP_FAILED)
						throw new Win32Exception(Marshal.GetLastWin32Error()); // Yes, this should work on unix too
					Unix.munmap(result, (UIntPtr)PAGE_SIZE);
				}
			}

			throw new PageAllocatorException("Failed to allocate memory in unused regions");
		}

		protected interface IMemoryMapper : IDisposable
		{
			bool FindNextFree(out IntPtr start, out IntPtr end);
		}

		private static class Unix
		{
			public static readonly IntPtr MAP_FAILED = new IntPtr(-1);

			[DynDllImport("mmap")]
			public static mmapDelegate mmap;

			[DynDllImport("munmap")]
			public static munmapDelegate munmap;

			public delegate IntPtr mmapDelegate(IntPtr addr, UIntPtr length, Protection prot, MapFlags flags, int fd, int offset);

			public delegate int munmapDelegate(IntPtr addr, UIntPtr length);

			[Flags]
			public enum MapFlags
			{
				MAP_PRIVATE = 0x02,
				MAP_ANONYMOUS = 0x20
			}

			[Flags]
			public enum Protection
			{
				PROT_READ = 0x1,
				PROT_WRITE = 0x2
			}

			static Unix()
			{
				typeof(Unix).ResolveDynDllImports(new Dictionary<string, List<DynDllMapping>>
				{
					["libc"] = new List<DynDllMapping>
					{
						"libc.so.6",               // Ubuntu glibc
						"libc",                    // Linux glibc,
						"/usr/lib/libSystem.dylib" // OSX POSIX
					}
				});
			}
		}
	}
}
