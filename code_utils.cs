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
        public static unsafe T KindaSafe_SuperCast<T>(byte[] data){ // double checks that the address is correct by comparing the actual size of the array with the size at he address, should not be needed.
            fixed(byte* data_ptr = &data[0])
                return *(T*)data_ptr; // this should be the correct way to do this?
        }
        /*
        public static unsafe T KindaSafe_SuperCast<T>(byte[] data) // double checks that the address is correct by comparing the actual size of the array with the size at he address, should not be needed.
        {
            ulong data_ptr = *(ulong*)&data;
            if (*(ulong*)(data_ptr + 0x8) == (ulong)data.Length) // 0x08 is the byte count
                return *(T*)(data_ptr + 0x10); // 0x10 is the start of the actual data
            throw new Exception("super cast failed, c# version issue?");
            return default(T);
        } */
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


        public static unsafe int CopyListStructTo<T>(List<T> _struct, byte[] buffer, int offset){
            int total_size = 0;
            for (int i = 0; i < _struct.Count(); i++)
                total_size += CopyStructTo(_struct[i], buffer, offset + (sizeof(T) * i));
            return total_size; // bit silly to do it this way because we could easily do a multi, but it works and thats enough
        }
        public static unsafe int CopyArrayStructTo<T>(T[] _struct, byte[] buffer, int offset){
            int total_size = 0;
            for (int i = 0; i < _struct.Length; i++)
                total_size += CopyStructTo(_struct[i], buffer, offset + (sizeof(T) * i));
            return total_size; // bit silly to do it this way because we could easily do a multi, but it works and thats enough
        }
        public static unsafe int CopyStructTo<T>(T _struct, byte[] buffer, int offset){
            if (buffer.Length <= sizeof(T) + offset)
                throw new Exception("not enough room to fit struct into buffer");
            // do a byte by byte copy, as otherwise we'd need to cast it to a byte array or something for it to work with the regular functions
            byte* source = (byte*)&_struct;
            for (int i = 0; i < sizeof(T); i++)
                buffer[i+offset] = source[i];
            return sizeof(T);
        }

    }
}
