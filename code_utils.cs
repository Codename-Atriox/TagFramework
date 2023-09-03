using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace Infinite_module_test
{
    static class code_utils
    {
        public static unsafe T SuperCast<T>(byte[] data) => *(T*)(*(ulong*)&data + 0x10);
        public static unsafe T SuperCast<T>(byte[] data, ulong startIndex) => *(T*)(*(ulong*)&data + (0x10 + startIndex));
        // DEBUG METHOD // DEBUG METHOD // DEBUG METHOD //
        public static unsafe T KindaSafe_SuperCast<T>(byte[] data) // double checks that the address is correct by comparing the actual size of the array with the size at he address, should not be needed.
        {
            ulong data_ptr = *(ulong*)&data;
            if (*(ulong*)(data_ptr + 0x8) == (ulong)data.Length) // 0x08 is the byte count
                return *(T*)(data_ptr + 0x10); // 0x10 is the start of the actual data
            throw new Exception("super cast failed, c# version issue?");
            return default(T);
        }
        // DEBUG METHOD // DEBUG METHOD // DEBUG METHOD //
        public static unsafe T KindaSafe_SuperCast<T>(byte[] data, ulong startIndex)
        {
            ulong data_ptr = *(ulong*)&data;
            if (*(ulong*)(data_ptr + 0x8) != (ulong)data.Length)
                throw new Exception("super cast failed, c# version issue?");
            //return default(T);

            // check to see if theres actually that many bytes in that array to read
            ulong struct_size = (ulong)Marshal.SizeOf(typeof(T));
            if (startIndex + struct_size > (ulong)data.Length)
                throw new Exception("super cast failed, c# version issue?");
            //return default(T);

            return *(T*)(data_ptr + (0x10 + startIndex));
        }


    }
}
