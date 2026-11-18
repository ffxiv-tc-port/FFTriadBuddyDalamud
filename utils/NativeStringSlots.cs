using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace MgAl2O4.Utils
{
    // Strings read from native NUL-terminated text, cached per caller-chosen slot: while a slot's bytes stay the same,
    // the string decoded last time is returned instead of a new one. Values always equal Marshal.PtrToStringUTF8/Ansi.
    // Not thread-safe; one instance per reader, used from the framework thread only.
    public class NativeStringSlots
    {
        private readonly bool isAnsi;
        private readonly List<byte[]?> slotBytes = new();
        private readonly List<string?> slotValues = new();

        public NativeStringSlots(bool isAnsi)
        {
            this.isAnsi = isAnsi;
        }

        public unsafe string? Read(int slot, byte* text)
        {
            if (text == null)
            {
                return null;
            }

            while (slotBytes.Count <= slot)
            {
                slotBytes.Add(null);
                slotValues.Add(null);
            }

            var bytes = MemoryMarshal.CreateReadOnlySpanFromNullTerminated(text);
            var cachedBytes = slotBytes[slot];
            if (cachedBytes != null && bytes.SequenceEqual(cachedBytes))
            {
                return slotValues[slot];
            }

            var value = isAnsi ? Marshal.PtrToStringAnsi((IntPtr)text) : Marshal.PtrToStringUTF8((IntPtr)text);
            slotBytes[slot] = bytes.ToArray();
            slotValues[slot] = value;
            return value;
        }
    }
}
