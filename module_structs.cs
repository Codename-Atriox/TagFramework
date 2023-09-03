﻿
using System.Collections;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Xml;
using static Infinite_module_test.code_utils;
using static Infinite_module_test.tag_structs;
using static System.Collections.Specialized.BitVector32;
using static System.Net.Mime.MediaTypeNames;

using OodleSharp;

// TODO:
// 1. separate loading into its own class, so that all data used is easily disposable
// 2. loading resource data, requires more testing to understand
// 3. tag saving, this will wait until season 3 as there was a few changes that simplify the process, and/or make it more complicated


namespace Infinite_module_test{

    public static class module_structs{
        public class module{
            // we have to simulate a file structure with modules, so our hierarchy works
            Dictionary<string, List<indexed_module_file>> file_groups = new();
            struct indexed_module_file{
                public indexed_module_file(string _name, int source_index, bool resource){
                    name = _name;
                    source_file_header_index = source_index;
                    is_resource = resource;}
                public string name;
                public int source_file_header_index;
                public bool is_resource;
            }

            public module_header module_info;
            public module_file[] files; // FileCount
            public byte[] string_table; // current_offset + tag.NameOffset = string* // StringsSize
            public int[] resource_table; // the function of this array is to index all of the tags that rely on resource things (not blocks), like models i think // ResourceCount
            public block_header[] blocks; // BlockCount

            string module_file_path; // idk why we'd need to store this

            FileStream module_reader; // so we can read from the module when calling tags
            long tagdata_base;
            public module(string module_path){
                module_file_path = module_path;
                // and then he said "it's module'n time"
                if (!File.Exists(module_file_path)) throw new Exception("failed to find specified module file"); // probably redundant
                
                using (module_reader = new FileStream(module_file_path, FileMode.Open, FileAccess.Read)){
                    // read module header
                    module_info = read_and_convert_to<module_header>(module_header_size);

                    // read module file headers
                    files = new module_file[module_info.FileCount];
                    for (int i = 0; i < files.Length; i++)
                        files[i] = read_and_convert_to<module_file>(module_file_size);

                    // read the string table
                    string_table = new byte[module_info.StringsSize];
                    module_reader.Read(string_table, 0, module_info.StringsSize);

                    // read the resource indicies?
                    resource_table = new int[module_info.ResourceCount];
                    for (int i = 0; i < resource_table.Length; i++)
                        resource_table[i] = read_and_convert_to<int>(4); // we should also fix this one too

                    // read the data blocks
                    blocks = new block_header[module_info.BlockCount];
                    for (int i = 0; i < blocks.Length; i++)
                        blocks[i] = read_and_convert_to<block_header>(block_header_size);

                    // now to read the compressed data junk

                    // align accordingly to 0x?????000 padding to read data
                    tagdata_base = (module_reader.Position / 0x1000 + 1) * 0x1000;
                    //module_reader.Seek(aligned_address, SeekOrigin.Begin);

                    // then we need to map out our directory, so the tools 
                    for (int i = 0; i < files.Length; i++){
                        module_file tag = files[i];
                        if (tag.ParentIndex != -1){ // resource file
                            // get parent tag so we can reference that for names
                            module_file par_tag = files[tag.ParentIndex];

                            // init group if it hasn't been already
                            string group = groupID_str(par_tag.ClassId);
                            if (!file_groups.ContainsKey(group))
                                file_groups.Add(group, new List<indexed_module_file>());

                            // get tag name // names list not implemented yet
                            string tagname = par_tag.GlobalTagId.ToString("X");
                            // figure out what index this resource is
                            int resource_index = -1;
                            for (int r = 0; r < par_tag.ResourceCount; r++){
                                if (resource_table[par_tag.ResourceIndex + r] == i){
                                    resource_index = r;
                                    break;
                            }}
                            tagname += "_res_" + resource_index;
                            file_groups[group].Add(new(tagname, i, true));

                        }else{ // a rewgular tag file
                            // init group if it hasn't been already
                            string group = groupID_str(tag.ClassId);
                            if (!file_groups.ContainsKey(group))
                                file_groups.Add(group, new List<indexed_module_file>());
                            // get tagname and add to directory
                            string tagname = tag.GlobalTagId.ToString("X");
                            file_groups[group].Add(new(tagname, i, false));
                    }}
                    // ok thats all, the tags have been read
                }
            }
            public byte[] get_module_file_bytes(module_file tag)
            {

                // read the flags to determine how to process this file
                bool using_compression = (tag.Flags & flag_UseCompression) != 0; // pretty sure this is true if reading_seperate_blocks is also true, confirmation needed
                bool reading_separate_blocks = (tag.Flags & flag_UseBlocks) != 0;
                bool reading_raw_file = (tag.Flags & flag_UseRawfile) != 0;

                byte[] decompressed_data = new byte[tag.TotalUncompressedSize];
                long data_Address = tagdata_base + tag.get_dataoffset();

                if (reading_separate_blocks){
                    for (int b = 0; b < tag.BlockCount; b++){
                        var bloc = blocks[tag.BlockIndex + b];
                        byte[] block_bytes;

                        if (bloc.Compressed == 1){
                            module_reader.Seek(data_Address + bloc.CompressedOffset, SeekOrigin.Begin);

                            byte[] bytes = new byte[bloc.CompressedSize];
                            module_reader.Read(bytes, 0, bytes.Length);
                            block_bytes = Oodle.Decompress(bytes, bytes.Length, bloc.UncompressedSize);
                        }else{ // uncompressed
                            module_reader.Seek(data_Address + bloc.UncompressedOffset, SeekOrigin.Begin);

                            block_bytes = new byte[bloc.UncompressedSize];
                            module_reader.Read(block_bytes, 0, block_bytes.Length);
                        }
                        System.Buffer.BlockCopy(block_bytes, 0, decompressed_data, bloc.UncompressedOffset, block_bytes.Length);

                }}else {  // is the manifest thingo, aka raw file, read data based off compressed and uncompressed length
                    module_reader.Seek(data_Address, SeekOrigin.Begin);
                    if (using_compression){
                        byte[] bytes = new byte[tag.TotalCompressedSize];
                        module_reader.Read(bytes, 0, bytes.Length);
                        decompressed_data = Oodle.Decompress(bytes, bytes.Length, tag.TotalUncompressedSize);
                    } else module_reader.Read(decompressed_data, 0, tag.TotalUncompressedSize);
                }

                return decompressed_data;
            }
            public byte[] get_tag_bytes(int tag_index){ // kinda redundant
                return get_module_file_bytes(files[tag_index]);
            }
            public List<byte[]> get_tag_resource_list(int tag_index){
                // get all resources & then read them into a list
                List<byte[]> output = new();
                module_file tag = files[tag_index];
                for (int i = 0; i < tag.ResourceCount; i++)
                    output.Add(get_module_file_bytes(files[resource_table[tag.ResourceIndex + i]]));
                
                return output;
            }
            // helper functions
            static string groupID_str(int groupid){
                string result = "";
                result += (char)((groupid >> 24) & 0xFF);
                result += (char)((groupid >> 16) & 0xFF);
                result += (char)((groupid >> 8) & 0xFF);
                result += (char)(groupid & 0xFF);
                return result.Trim().Replace('*', '_');
            }
            private T read_and_convert_to<T>(int read_length){
                byte[] bytes = new byte[read_length];
                module_reader.Read(bytes, 0, read_length);
                return KindaSafe_SuperCast<T>(bytes);
            }
        }

        // /////////////////// //
        // struct definitions //
        // ///////////////// //
        public const int module_header_size = 0x50;
        [StructLayout(LayoutKind.Explicit, Size = module_header_size)]
        public struct module_header
        {
            [FieldOffset(0x00)] public int Head;           //  used to determine if this file is actually a module, should be "mohd"
            [FieldOffset(0x04)] public int Version;        //  48 flight1, 51 flight2 & retail
            [FieldOffset(0x08)] public ulong ModuleId;       //  randomized between modules, algo unknown
            [FieldOffset(0x10)] public int FileCount;      //  the total number of tags contained by the module

            [FieldOffset(0x14)] public int ManifestCount;       //  'FFFFFFFF' "Number of tags in the load manifest (0 if the module doesn't have one, see the "Load Manifest" section below)"
            [FieldOffset(0x18)] public int Manifest_Unk_0x18;   //  'FFFFFFFF' on blank modules, 0 on non blanks, assumedly this the index of the manifest file in the module files array
            [FieldOffset(0x1C)] public int Manifest_Unk_0x1C;   //  'FFFFFFFF'

            [FieldOffset(0x20)] public int ResourceIndex;   //  "Index of the first resource entry (numFiles - numResources)"
            [FieldOffset(0x24)] public int StringsSize;     //  total size (in bytes) of the strings table
            [FieldOffset(0x28)] public int ResourceCount;   //  number of resource files
            [FieldOffset(0x2C)] public int BlockCount;      //  number of data blocks

            [FieldOffset(0x30)] public ulong BuildVersion;    // this should be the same between each module
            [FieldOffset(0x38)] public ulong Checksum;        // "Murmur3_x64_128 of the header (set this field to 0 first), file list, resource list, and block list"
            // new with infinite
            [FieldOffset(0x40)] public int Unk_0x040;       //  0
            [FieldOffset(0x44)] public int Unk_0x044;       //  0
            [FieldOffset(0x48)] public int Unk_0x048;       //  2
            [FieldOffset(0x4C)] public int Unk_0x04C;       //  0
        }

        public const int module_file_size = 0x58;
        [StructLayout(LayoutKind.Explicit, Size = module_file_size)]
        public struct module_file
        {
            public long get_dataoffset(){
                return (long)(DataOffset_and_flags & 0x0000FFFFFFFFFFFF);}
            public ushort get_dataflags(){ // NOTE: only the last 8 bits are actually flags, the other (first) 8 bits are some kind of counter
                return (ushort)(DataOffset_and_flags >> 48);}
            [FieldOffset(0x00)] public byte ClassGroup;     //  
            [FieldOffset(0x01)] public byte Flags;          // refer to flag bits below this struct
            [FieldOffset(0x02)] public ushort BlockCount;     // "The number of blocks that make up the file. Only valid if the HasBlocks flag is set"
            [FieldOffset(0x04)] public uint BlockIndex;     // "The index of the first block in the file. Only valid if the HasBlocks flag is set"
            [FieldOffset(0x08)] public uint ResourceIndex;  // "Index of the first resource in the module's resource list that this file owns"

            [FieldOffset(0x0C)] public int ClassId;        // this is the tag group, should be a string right?

            [FieldOffset(0x10)] public ulong DataOffset_and_flags;     // for now just read as a long // wow we were not infact reading this a long
            //[FieldOffset(0x14)] public uint    Unk_0x14;       // we will now need to double check each file to make sure if this number is ever anything // its used in the very big files

            [FieldOffset(0x18)] public int TotalCompressedSize;    // "The total size of compressed data."
            [FieldOffset(0x1C)] public int TotalUncompressedSize;  // "The total size of the data after it is uncompressed. If this is 0, then the file is empty."

            [FieldOffset(0x20)] public int GlobalTagId;   // this is the murmur3 hash; autogenerate from tag path

            [FieldOffset(0x24)] public int UncompressedHeaderSize;
            [FieldOffset(0x28)] public int UncompressedTagDataSize;
            [FieldOffset(0x2C)] public int UncompressedResourceDataSize;
            [FieldOffset(0x30)] public int UncompressedActualResourceDataSize;   // used with bitmaps, and likely other tags idk

            [FieldOffset(0x34)] public byte HeaderAlignment;             // Power of 2 to align the header buffer to (e.g. 4 = align to a multiple of 16 bytes).
            [FieldOffset(0x35)] public byte TagDataAlightment;           // Power of 2 to align the tag data buffer to.
            [FieldOffset(0x36)] public byte ResourceDataAligment;        // Power of 2 to align the resource data buffer to.
            [FieldOffset(0x37)] public byte ActualResourceDataAligment;  // Power of 2 to align the actual resource data buffer to.

            [FieldOffset(0x38)] public uint NameOffset;       // 
            [FieldOffset(0x3C)] public int ParentIndex;      // "Used with resources to point back to the parent file. -1 = none"
            [FieldOffset(0x40)] public ulong AssetChecksum;    // "Murmur3_x64_128 hash of (what appears to be) the original file that this file was built from. This is not always the same thing as the file stored in the module. Only verified if the HasBlocks flag is not set."
            [FieldOffset(0x48)] public ulong AssetId;          // "The asset ID (-1 if not a tag)." maybe other files reference this through its id?

            [FieldOffset(0x50)] public uint ResourceCount;  // "Number of resources this file owns"
            [FieldOffset(0x54)] public int Unk_0x54;       // so far has just been 0, may relate to hd files?
        }
        // 'Flags' // 
        const byte flag_UseCompression = 0b00000001; // Uses Compression
        const byte flag_UseBlocks      = 0b00000010; // has blocks, which means to read the data across several data blocks, otherwise read straight from data offset
        const byte flag_UseRawfile     = 0b00000100; // is a raw file, meaning it has no tag header

        public const int block_header_size = 0x14;
        [StructLayout(LayoutKind.Explicit, Size = block_header_size)]
        public struct block_header // sizeof = 0x14
        {
            // these SHOULD be uints, however oodle does not like that, so if we get any issues here blame it on oodle
            // so max decompression size is 2gb, which is unlikely that we'll breach that so this is ok for now
            [FieldOffset(0x00)] public int CompressedOffset;
            [FieldOffset(0x04)] public int CompressedSize;
            [FieldOffset(0x08)] public int UncompressedOffset;
            [FieldOffset(0x0C)] public int UncompressedSize;
            [FieldOffset(0x10)] public int Compressed;
        }
    }


    public static class tag_structs
    {
        // VERSION 27 // VERSION 27 // VERSION 27 //
        // an interesting thing to note is that version 27 was the version of halo 5 forge
        // it seems someone on the engine team failed to update this number, because this struct has certainly changed since
        // or at least its child structs definitely changed
        /////////////// //////////////////////////////////////////////
        // TAG STUFF // //////////////////////////////////////////////
        /////////////// //////////////////////////////////////////////
        //    _____________         ___            ____________
        //   |             |       /   \          /            \ 
        //   |_____   _____|      /     \        /    ______    \ 
        //        |   |          /  /\   \      /    /      \____\ 
        //        |   |         /  /__\   \     |   /     ________
        //        |   |        /   ____    \    |   \     |___    |
        //        |   |       /   /     \   \   \    \______/    /
        //        |   |      /   /       \   \   \              /
        //        |___|     /___/         \___\   \____________/
        //                            
        // currently having this as a class, so that we can just copy pointers to this structure for effiency
        public class tag {
            public tag(string _plugin_path, List<KeyValuePair<byte[], bool>>? resources, XmlNode _reference_root = null)
            {
                resource_list = resources;
                reference_root = _reference_root;

                plugin_path = _plugin_path;
            }
            // load tag outputs
            public bool Initialized = false;
            public tagdata_struct? root = null;
            public XmlNode reference_root;
            //
            string plugin_path;
            //
            XmlDocument reference_xml;
            // resource path, is whole file
            List<KeyValuePair<byte[], bool>> resource_list;
            int processed_resource_index = 0; // we use this to keep track of which files we've opened
            private T read_and_convert_to<T>(int read_length) {
                byte[] bytes = new byte[read_length];
                tag_reader.Read(bytes, 0, read_length);
                return KindaSafe_SuperCast<T>(bytes);
            }
            private T[] struct_array_assign_bytes<T>(int count, int struct_size) {
                T[] output = new T[count];
                for (int i = 0; i < count; i++)
                    output[i] = read_and_convert_to<T>(struct_size);
                return output;
            }
            private byte[] read_at(long position, int length) {
                byte[] bytes = new byte[length];
                tag_reader.Seek(position, SeekOrigin.Begin);
                tag_reader.Read(bytes, 0, length);
                return bytes;
            }
            MemoryStream? tag_reader; // to be cleaned up after loading
            public bool Load_tag_file(byte[] tag_bytes, string target_guid = "") {
                //if (!File.Exists(tag_path)) return false; // failed via invalid directory
                tag_reader = new MemoryStream(tag_bytes);
                // read the first 4 bytes to make sure this is a tag file
                byte[] header_test = new byte[4];
                tag_reader.Read(header_test, 0, 4);
                if (Encoding.UTF8.GetString(header_test) != "ucsh") return false; // failed due to not a tag file
                // ok begin parsing the tag
                tag_reader.Seek(0, SeekOrigin.Begin); // reset position
                header = read_and_convert_to<tag_header>(tag_header_size);
                // read tag dependencies
                dependencies = struct_array_assign_bytes<tag_dependency>(header.DependencyCount, tag_dependency_size);
                // read tag data blocks
                data_blocks = struct_array_assign_bytes<data_block>(header.DataBlockCount, data_block_size);
                // read tag ref structures
                tag_structs = struct_array_assign_bytes<tag_def_structure>(header.TagStructCount, tag_def_structure_size);
                // process that array into a faster dictionary
                struct_links = new Dictionary<uint, uint>[tag_structs.Length];
                // initialize each element in the array
                for (int i = 0; i < tag_structs.Length; i++) struct_links[i] = new();
                // write an array of data block's parent structs, so that we may find a structs parent through the block it resides inside of
                int[] block_to_struct_links = new int[data_blocks.Length];

                for (uint i = 0; i < block_to_struct_links.Length; i++) block_to_struct_links[i] = -1; // placeholdewr until we hfigure out how to do this normally

                for (uint i = 0; i < tag_structs.Length; i++) {
                    if (tag_structs[i].TargetIndex == -1 || tag_structs[i].Type == 2 || tag_structs[i].Type == 4) continue; // either a null struct or is a resource struct
                    // FU joe halo, why are some of your tags poorly formatted, where tags structs can have the same target index
                    if (block_to_struct_links[tag_structs[i].TargetIndex] != -1) continue; // FAILSAFE

                    block_to_struct_links[tag_structs[i].TargetIndex] = (int)i;
                }
                // assign the child structs links, based on their parent field block
                for (uint i = 0; i < tag_structs.Length; i++) {
                    if (tag_structs[i].Type == 0 || tag_structs[i].Type == 4) continue; // the root struct has no parent, AND TYPE 4'S OVERLAP WITH OTHER PARAMS??
                    var ven = block_to_struct_links[tag_structs[i].FieldBlock];
                    var ben = struct_links[ven];
                    ben.Add(tag_structs[i].FieldOffset, i);
                }
                // read tag data references?
                data_references = struct_array_assign_bytes<data_reference>(header.DataReferenceCount, data_reference_size);
                // now write the links array, so that we can easily find out which struct owns each data
                data_links = new Dictionary<uint, uint>[tag_structs.Length];
                // initialize each element in the array
                for (uint i = 0; i < tag_structs.Length; i++) data_links[i] = new();
                for (uint i = 0; i < data_references.Length; i++)
                {
                    data_links[data_references[i].ParentStructIndex].Add(data_references[i].FieldOffset, i);
                }

                // TAG STRUCTS ALSO REFER TO RESOURCE FILE STRUCTS!!!!!!!!!!!!!

                // read tag tag fixup references 
                tag_fixup_references = struct_array_assign_bytes<tag_fixup_reference>(header.TagReferenceCount, tag_fixup_reference_size);
                // assign the string table bytes, wow this is not convienent at all lol // more or less now more convenient
                if (header.StringTableSize > 0) {
                    //local_string_table = new byte[header.StringTableSize];
                    local_string_table = new byte[header.StringTableSize];
                    tag_reader.Read(local_string_table, 0, (int)header.StringTableSize);
                }
                // read the zoneset header
                zoneset_info = read_and_convert_to<zoneset_header>(zoneset_header_size);
                /* // ZONESETS SEEM TO BE NULLED (BD'd) OUT???
                // read all the zoneset instances
                zonesets = new zoneset_instance[zoneset_info.ZonesetCount];
                // its literally not possible for that to be a null reference, we just set it above
                for (int m = 0; m < zonesets.Length; m++) {
                    // read the header
                    zonesets[m].header = read_and_convert_to<zoneset_instance_header>(zoneset_instance_header_size);
                    // read the regular zoneset tags
                    zonesets[m].zonset_tags = struct_array_assign_bytes<zoneset_tag>(zonesets[m].header.TagCount, zoneset_tag_size);
                    // read the zoneset footer tags (whatever they are?)
                    zonesets[m].zonset_footer_tags = struct_array_assign_bytes<zoneset_tag>(zonesets[m].header.FooterCount, zoneset_tag_size);
                    // read the parents
                    zonesets[m].zonset_parents = struct_array_assign_bytes<int>(zonesets[m].header.ParentCount, 4);
                }
                */
                // end of header, double check to make sure we read it all correctly // APPARENTLY THERES A LOT OF CASES WITH DATA THAT WE DONT READ !!!!!!!!!!!!!! FU BUNGIE
                // read tag header data (so we can access any tag data that gets stored at the end)
                header_data = read_at(0, (int)header.HeaderSize);
                // read tag data
                if (header.DataSize > 0) tag_data = read_at(header.HeaderSize, (int)header.DataSize);
                // read resource data
                if (header.ResourceDataSize > 0) tag_resource = read_at(header.HeaderSize + header.DataSize, (int)header.ResourceDataSize);
                // read actual resource data
                if (header.ActualResoureDataSize > 0) actual_tag_resource = read_at(header.HeaderSize + header.DataSize + header.ResourceDataSize, (int)header.ActualResoureDataSize);

                // cleanup tag reading
                tag_reader.Dispose();
                tag_reader = null;
                // and now its time to process that all into proper usable data, 
                // for the time being, we'll keep all that as global variables,
                // however when we begin on tag recompiling, we'll make the majority into temp vars
                // so basically we have all these global variables because 




                // loop through all of the structs until we find the one that is the main struct (type == 0)
                if (reference_root == null){
                    for (uint i = 0; i < tag_structs.Length; i++){
                        if (tag_structs[i].Type == 0) {
                            // because of how this system works, we need to find the GUID first, to figure out what plugin file to use
                            // realistically, we could rename the plugins as their guid value, but that wouldn't be very cool for the people just looking through the files (aka not user friendly)
                            string root_GUID = tag_structs[i].GUID_1.ToString("X16") + tag_structs[i].GUID_2.ToString("X16");
                            string plugin_guide_path = plugin_path + "\\GUIDs.txt";
                            string tagus_groupus = "";
                            using (StreamReader reader = File.OpenText(plugin_guide_path)){
                                while (!reader.EndOfStream) {
                                    string[] line = reader.ReadLine().Split(":");
                                    if (line[1] == root_GUID) {
                                        tagus_groupus = line[0];
                                        break;
                            }}}


                            // setup reference plugin file
                            reference_xml = new XmlDocument();
                            reference_xml.Load(plugin_path + "\\" + tagus_groupus + ".xml");
                            //boingo zoingo; // add error handling here or something
                            reference_root = reference_xml.SelectSingleNode("root");


                            root = process_highlevel_struct(i, root_GUID);
                            break;
                }}}else{ // is a subtag
                    // reference_xml = inherited;
                    // reference_root = inherited;
                    for (uint i = 0; i < tag_structs.Length; i++){
                        if (tag_structs[i].Type == 0){
                            root = process_highlevel_struct(i, target_guid);
                            break;
                }}}



                Initialized = true;
                return true;
            }
            public class tagdata_struct
            {
                // GUID needed, as we'll use that to figure out which struct to reference when interpretting the byte array
                public string GUID;
                public List<thing> blocks = new();
            }
            public struct thing
            {
                // array of the struct bytes
                public byte[] tag_data;
                // offset, tagblock instance
                public Dictionary<ulong, tagdata_struct> tag_block_refs;
                // offset, resource data
                public Dictionary<ulong, byte[]> tag_resource_refs;
                // we should also have the tag ref dictionaries
                // offset, resourfce file instance
                public Dictionary<ulong, tagdata_struct> resource_file_refs;
            }
            private tagdata_struct? process_highlevel_struct(uint struct_index, string GUID, int block_count = 1)
            {
                tagdata_struct output_struct = new();
                output_struct.GUID = GUID;
                // test if this struct is null, nothing needs to be read if it is
                if (tag_structs[struct_index].TargetIndex == -1) return output_struct;


                //string GUID = tag_structs[struct_index].GUID_1.ToString("X16") + tag_structs[struct_index].GUID_2.ToString("X16");
                // now we can select the struct
                XmlNode currentStruct = reference_root.SelectSingleNode("_" + GUID);

                int referenced_array_size = Convert.ToInt32(currentStruct.Attributes["Size"].Value, 16);

                data_block struct_file_offset = data_blocks[tag_structs[struct_index].TargetIndex];
                byte[] tag_datas = return_referenced_byte_segment(struct_file_offset.Section);

                for (int i = 0; i < block_count; i++)
                {
                    var test = new thing();
                    test.tag_data = tag_datas.Skip((int)struct_file_offset.Offset + (referenced_array_size * i)).Take(referenced_array_size).ToArray();
                    test.tag_block_refs = new();
                    test.tag_resource_refs = new();
                    test.resource_file_refs = new();

                    // WE NEED TO DO THE OFFSET, BECAUSE TAGBLOCKS ARE STORED IN THE SAME TAGDATA REGION
                    // ELSE ALL THE TAGBLOCKS WILL HAVE THE SAME CONTENTS (or same counts)
                    process_literal_struct(currentStruct, struct_index, ref test, (ulong)(referenced_array_size * i), 0);

                    output_struct.blocks.Add(test);
                }
                return output_struct;
            }
            private byte[] return_referenced_byte_segment(ushort section) {
                if (section == 0) return header_data;
                else if (section == 1) return tag_data;
                else if (section == 2) return tag_resource;
                else if (section == 3) return actual_tag_resource;
                else return null; // we should never hit this block
            }
            private int read_int_from_array_at(ushort section, int offset) {
                if (section == 0) return BitConverter.ToInt32(header_data, offset);
                else if (section == 1) return BitConverter.ToInt32(tag_data, offset);
                else if (section == 2) return BitConverter.ToInt32(tag_resource, offset);
                else if (section == 3) return BitConverter.ToInt32(actual_tag_resource, offset);
                else return -1; // we should never hit this block
            }
            private void process_literal_struct(XmlNode structparent, uint struct_index, ref thing append_to, ulong current_offset, ulong fixed_offset)
            {


                for (int i = 0; i < structparent.ChildNodes.Count; i++)
                {
                    XmlNode currentParam = structparent.ChildNodes[i];
                    if (currentParam.Name != "_38"
                     && currentParam.Name != "_39"
                     && currentParam.Name != "_40"
                     && currentParam.Name != "_42"
                     && currentParam.Name != "_43")
                        continue;

                    ulong relative_offset = (ulong)Convert.ToInt32(currentParam.Attributes["Offset"].Value, 16);
                    ulong data_block_offset = relative_offset + current_offset;
                    ulong tagblock_constant_offset = relative_offset + fixed_offset;

                    // write the node, then write attributes
                    switch (currentParam.Name)
                    {
                        case "_38":
                            { // _field_struct - 0byte
                                string xml_guid = "_" + currentParam.Attributes["GUID"].Value;
                                XmlNode struct_node = reference_root.SelectSingleNode(xml_guid);
                                process_literal_struct(struct_node, struct_index, ref append_to, data_block_offset, tagblock_constant_offset);
                            }
                            break;
                        case "_39":
                            { // _field_array - 0byte
                                string xml_guid = "_" + currentParam.Attributes["GUID"].Value;
                                int array_count = Convert.ToInt32(currentParam.Attributes["Count"].Value);
                                XmlNode struct_node = reference_root.SelectSingleNode(xml_guid);
                                int referenced_array_size = Convert.ToInt32(struct_node.Attributes["Size"].Value, 16); // WHY ARE THESE STORED IN HEXADECIMAL FORMAT
                                for (int array_ind = 0; array_ind < array_count; array_ind++){
                                    ulong next_offset = data_block_offset + (ulong)(referenced_array_size * array_ind);
                                    ulong next_fixed_offset = tagblock_constant_offset + (ulong)(referenced_array_size * array_ind);
                                    process_literal_struct(struct_node, struct_index, ref append_to, next_offset, next_fixed_offset);
                            }}
                            break;
                        case "_40":
                            { // _field_tag_block - 20byte
                              // read the count
                              // find the struct that this is referring to
                                string struct_guid = currentParam.Attributes["GUID"].Value;
                                ulong param_offset = data_block_offset + data_blocks[tag_structs[struct_index].TargetIndex].Offset;
                                int tagblock_count = read_int_from_array_at(data_blocks[tag_structs[struct_index].TargetIndex].Section, (int)(param_offset + 16));
                                uint next_struct_index = struct_links[struct_index][(uint)data_block_offset];
                                //tag_def_structure next_struct = tag_structs[];
                                append_to.tag_block_refs.Add(tagblock_constant_offset, process_highlevel_struct(next_struct_index, struct_guid, tagblock_count));
                            }
                            break;
                        // we should also do one of these arry dictionary thingos for tag references _41
                        case "_42": // _field_data - 24byte
                            {   // for this, you're supposed to find the corresponding 'data_references' index, which will tell us which data block to read via the 'target_index'
                                // wow thats way too many steps
                                uint data_ref_index = data_links[struct_index][(uint)data_block_offset];
                                data_reference data_header = data_references[data_ref_index];
                                if (data_header.TargetIndex != -1)
                                {
                                    data_block data_data = data_blocks[data_header.TargetIndex];
                                    byte[] data_bytes = return_referenced_byte_segment(data_data.Section).Skip((int)data_data.Offset).Take((int)data_data.Size).ToArray();
                                    if (data_data.Unk_0x04 != 0)
                                    {

                                    }
                                    append_to.tag_resource_refs.Add(tagblock_constant_offset, data_bytes);
                                }
                                else append_to.tag_resource_refs.Add(tagblock_constant_offset, new byte[0]);
                            }
                            break;
                        case "_43": // _field_resource - 16byte
                            // hmm, this section works basically exactly the same as tagblocks, except these refer to files outside of this file
                            {

                                string struct_guid = currentParam.Attributes["GUID"].Value;
                                ulong param_offset = data_block_offset + data_blocks[tag_structs[struct_index].TargetIndex].Offset;
                                int resource_type = read_int_from_array_at(data_blocks[tag_structs[struct_index].TargetIndex].Section, (int)(param_offset + 12));
                                if (resource_type == 0)  // external tag resource type // UNKOWN HOW TO READ AS OF RIGHT NOW
                                {
                                    //uint resource_struct_index = struct_links[struct_index][(uint)data_block_offset];
                                    //tag_def_structure test = tag_structs[resource_struct_index];
                                    //data_block data_data = data_blocks[test.TargetIndex];
                                    //byte[] data_bytes = return_referenced_byte_segment(data_data.Section).Skip((int)data_data.Offset).Take((int)data_data.Size).ToArray();
                                    //string bitles = BitConverter.ToString(data_bytes).Replace('-', ' ');
                                    
                                    tag child_tag = new(plugin_path, null, reference_root);
                                    if (resource_list[processed_resource_index].Value == false)
                                    { // this is an ERROR, but we do not care because w;'

                                    }
                                    if (!child_tag.Load_tag_file(resource_list[processed_resource_index].Key, struct_guid))
                                    { // also an error

                                    }
                                    append_to.resource_file_refs.Add(tagblock_constant_offset, child_tag.root);
                                    processed_resource_index++;
                                }
                                else // chunked resource type
                                {
                                    uint next_struct_index = struct_links[struct_index][(uint)data_block_offset];
                                    append_to.resource_file_refs.Add(tagblock_constant_offset, process_highlevel_struct(next_struct_index, struct_guid));
                                }

                                //string struct_guid = currentParam.Attributes["GUID"].Value;
                                //ulong param_offset = relative_offset + data_blocks[tag_struct.TargetIndex].Offset;
                                //int tagblock_count = KindaSafe_SuperCast<int>(return_referenced_byte_segment(data_blocks[tag_struct.TargetIndex].Section), param_offset + 16);

                                //tag_def_structure next_struct = structs[struct_guid];
                                //append_to.tag_block_refs.Add(relative_offset, process_highlevel_struct(ref next_struct, tagblock_count));
                            }
                            break;
                    }
                }
            }

            // not nullable because we are not checking if its not null 100 times
            public tag_header header;
            public tag_dependency[]? dependencies; // header.DependencyCount
            public data_block[]? data_blocks; // header.DataBlockCount
            // offset , child struct index
            public Dictionary<uint, uint>[] struct_links;
            public tag_def_structure[]? tag_structs; // header.TagStructCount
            // offset , child data_references index
            public Dictionary<uint, uint>[] data_links;
            public data_reference[]? data_references; // header.DataReferenceCount
            public tag_fixup_reference[]? tag_fixup_references; // header.TagReferenceCount

            //public string_id_reference[] string_id_references; // potentially unused? double check
            public byte[]? local_string_table; // header.StringTableSize
            // also non-nullable so we dont have to check if its null or not
            public zoneset_header zoneset_info;
            public zoneset_instance[]? zonesets; // zoneset_info.ZoneSetCount

            // and non-required stuff i guess
            public byte[]? tag_data; // like the actual values that the tag holds, eg. projectile speed and all those thingos
            public byte[]? tag_resource; // ONLY SEEN TO BE USED IN MAT FILES
            public byte[]? actual_tag_resource; // used in bitmap files

            public byte[]? raw_file_bytes; // used for reading raw files


            //public byte[]? unmapped_header_data; // used for debugging unmapped structures in headers
            public byte[]? header_data; // tag structs require us to store the WHOLE header data so the offsets match
            // we could likely setup something so we only use the unmarked and subtract the 
        }

        public const int tag_header_size = 0x50;
        [StructLayout(LayoutKind.Explicit, Size = tag_header_size)] public struct tag_header
        {
            [FieldOffset(0x00)] public int     Magic; 
            [FieldOffset(0x04)] public uint    Version; 
            [FieldOffset(0x08)] public ulong   Unk_0x08; 
            [FieldOffset(0x10)] public ulong   AssetChecksum;  
            // these cant be uints because of how the code is setup, should probably assert if -1
            [FieldOffset(0x18)] public int     DependencyCount; 
            [FieldOffset(0x1C)] public int     DataBlockCount;  
            [FieldOffset(0x20)] public int     TagStructCount; 
            [FieldOffset(0x24)] public int     DataReferenceCount; 
            [FieldOffset(0x28)] public int     TagReferenceCount; 
                                
            [FieldOffset(0x2C)] public uint    StringTableSize; 
                                
            [FieldOffset(0x30)] public uint    ZoneSetDataSize;    // this is the literal size in bytes
            [FieldOffset(0x34)] public uint    Unk_0x34; // new with infinite, cold be an enum of how to read the alignment bytes // seems to be chunked resource file count
                                
            [FieldOffset(0x38)] public uint    HeaderSize; 
            [FieldOffset(0x3C)] public uint    DataSize; 
            [FieldOffset(0x40)] public uint    ResourceDataSize; 
            [FieldOffset(0x44)] public uint    ActualResoureDataSize;  // also new with infinite
                                
            [FieldOffset(0x48)] public byte    HeaderAlignment; 
            [FieldOffset(0x49)] public byte    TagDataAlightment; 
            [FieldOffset(0x4A)] public byte    ResourceDataAligment; 
            [FieldOffset(0x4B)] public byte    ActualResourceDataAligment; 

            [FieldOffset(0x4C)] public int     Unk_0x4C; 
        }


        public const int tag_dependency_size = 0x18;
        [StructLayout(LayoutKind.Explicit, Size = tag_dependency_size)] public struct tag_dependency // this struct looks just like a regular tag reference
        {
            [FieldOffset(0x00)] public int     GroupTag;
            [FieldOffset(0x04)] public uint    NameOffset;
                                
            [FieldOffset(0x08)] public long    AssetID;
            [FieldOffset(0x10)] public int     GlobalID;
            [FieldOffset(0x14)] public int     Unk_0x14;   // possibly padding?
        }

        public const int data_block_size = 0x10;
        [StructLayout(LayoutKind.Explicit, Size = data_block_size)] public struct data_block
        {
            [FieldOffset(0x00)] public uint    Size;
            [FieldOffset(0x04)] public ushort  Unk_0x04;   // "(0 - 14, probably an enum)", potentially the index of the resource file
                                
            [FieldOffset(0x06)] public ushort  Section;    // "0 = Header, 1 = Tag Data, 2 = Resource Data" 3 would be that actual resource thingo
            [FieldOffset(0x08)] public ulong   Offset;     // "The offset of the start of the data block, relative to the start of its section."
        }

        public const int tag_def_structure_size = 0x20;
        [StructLayout(LayoutKind.Explicit, Size = tag_def_structure_size)] public struct tag_def_structure
        {
            //byte[16] GUID; // 0x00 // ok lets not attempt to use a byte array then
            [FieldOffset(0x00)] public long   GUID_1;
            [FieldOffset(0x08)] public long   GUID_2;
                                
            [FieldOffset(0x10)] public ushort  Type;           // "0 = Main Struct, 1 = Tag Block, 2 = Resource, 3 = Custom" NOTE: THERE IS A NUMBER 4, UNKNOWN WHAT ITS PURPOSE IS
            [FieldOffset(0x12)] public ushort  Unk_0x12;       // likely padding
                                
            [FieldOffset(0x14)] public int     TargetIndex;    // "For Main Struct and Tag Block structs, the index of the block containing the struct. For Resource structs, this (probably) is the index of the resource. This can be -1 if the tag field doesn't point to anything (null Tag Blocks or Custom structs)."
            [FieldOffset(0x18)] public int     FieldBlock;     // "The index of the data block containing the tag field which refers to this struct. Can be -1 for the Main Struct."
            [FieldOffset(0x1C)] public uint    FieldOffset;    // "The offset of the tag field inside the data block. (Unlike in Halo Online, this points to the tag field, not the pointer inside the tag field. Some tag fields for structs don't even have a pointer.)"
        }
        // further reading:https://github.com/ElDewrito/AusarDocs/blob/master/FileFormats/CachedTag.md#main-struct-type-0


        public const int data_reference_size = 0x14;
        [StructLayout(LayoutKind.Explicit, Size = data_reference_size)] public struct data_reference
        {
            [FieldOffset(0x00)] public int     ParentStructIndex;  // "The index of the tag struct containing the tag field."
            [FieldOffset(0x04)] public int     Unk_0x04; 
                                
            [FieldOffset(0x08)] public int     TargetIndex;        // "The index of the data block containing the referenced data. Can be -1 for null references."
            [FieldOffset(0x0C)] public int     FieldBlock;         // "The index of the data block containing the tag field."
            [FieldOffset(0x10)] public uint    FieldOffset;        // "The offset of the tag field inside the data block."
        }

        public const int tag_fixup_reference_size = 0x10;
        [StructLayout(LayoutKind.Explicit, Size = tag_fixup_reference_size)] public struct tag_fixup_reference
        {
            [FieldOffset(0x00)] public int     FieldBlock;     // "The index of the data block containing the tag field."
            [FieldOffset(0x04)] public uint    FieldOffset;    // "The offset of the tag field inside the data block. (Unlike in Halo Online, this points to the tag field, not the handle inside the tag field.)"
                                
            [FieldOffset(0x08)] public uint    NameOffset;     // "The offset of the tag filename within the String Table."
            [FieldOffset(0x0C)] public int     DepdencyIndex;  // "The index of the tag dependency in the Tag Dependency List. Can be -1 for null tag references."
        }

        
        // zoneset junk

        public const int zoneset_header_size = 0x10;
        [StructLayout(LayoutKind.Explicit, Size = zoneset_header_size)] public struct zoneset_header
        {
            [FieldOffset(0x00)] public int     Unk_0x00;       // could be the version??? this value seems to be the same between all entrants
            [FieldOffset(0x04)] public int     ZonesetCount;   // the total amount of zoneset instances to read
            [FieldOffset(0x08)] public int     Unk_0x08;       // potentially the sum of all the zonests footer counts?
            [FieldOffset(0x0C)] public int     Unk_0x0C;       // potentially the sum of all the zonesets parents?
        }

        public const int zoneset_instance_header_size = 0x10;
        [StructLayout(LayoutKind.Explicit, Size = zoneset_instance_header_size)] public struct zoneset_instance_header
        {
            [FieldOffset(0x00)] public int     StringID;       // the name of the zoneset that this tag should belong to?
            [FieldOffset(0x04)] public int     TagCount;       // "Number of tags to load for the zoneset"
            [FieldOffset(0x08)] public int     ParentCount;    // the count of 4 byte items that come after voth the tags, and footer tags, potentially parent zoneset??
            [FieldOffset(0x0C)] public int     FooterCount;    // seems to be the same struct as tagCount
        }

        public struct zoneset_instance
        {
            public zoneset_instance_header header;
            public zoneset_tag[] zonset_tags;
            public zoneset_tag[] zonset_footer_tags;
            public int[] zonset_parents;
        }

        public const int zoneset_tag_size = 0x08;
        [StructLayout(LayoutKind.Explicit, Size = zoneset_tag_size)] public struct zoneset_tag
        {
            [FieldOffset(0x00)] public uint    GlobalID;   // id of the tag? why are we referencing other tags???
            [FieldOffset(0x04)] public int     StringID;   // name of the zoneset (as a hash?)
        }
    }

}
