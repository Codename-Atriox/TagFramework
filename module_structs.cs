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
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata;
using System.Reflection;
using System.Text.Unicode;
using System;
using System.ComponentModel;
using System.Xml.Linq;
using System.Collections.Generic;
using static System.Reflection.Metadata.BlobBuilder;
using System.Diagnostics.SymbolStore;
using System.Net.NetworkInformation;
using System.Numerics;

// TODO:
// 1. separate loading into its own class, so that all data used is easily disposable
// 2. loading resource data, requires more testing to understand
// 3. tag saving, this will wait until season 3 as there was a few changes that simplify the process, and/or make it more complicated


namespace Infinite_module_test{
    public static class MapInfo
    {
        static byte[] mapinfo_pattern = new byte[4] { 0x61, 0x6E, 0x79, 0x5C };
        static public List<module_structs.module> open_mapinfo(string path)
        {
            // we always assume the mapinfos are where 343 put them
            // so we can split the path to find the directory to the module files

            string halo_infinite_path = path.Split("__cms__")[0];
            List<module_structs.module> result = new();

            // open & process mapinfo file
            // read the garbage
            byte[] bytes = File.ReadAllBytes(path);

            //int name_length = bytes[3];

            //int offset_to_name_size = 4 + name_length + 0xc;
            //int path_length = bytes[offset_to_name_size];

            //int offset_to_module_files = offset_to_name_size + 1 + path_length + 0x3c;

            //int modules_count = bytes[offset_to_module_files];
            //offset_to_module_files++;
            // we are just going to find the first module and do the offsets from there
            int first_offset = 0;
            while (true){
                if (first_offset + 4 >= bytes.Length)
                    throw new Exception("could not find matching pattern in mapinfo.");
                if (bytes[first_offset..(first_offset+4)].SequenceEqual(mapinfo_pattern))
                    break;
                first_offset++;
            }
            int modules_count = bytes[first_offset-2];
            int current_offset = first_offset-1;
            for (int i = 0; i < modules_count; i++){
                int string_length = bytes[current_offset];
                current_offset++;
                string module = System.Text.Encoding.UTF8.GetString(bytes, current_offset, string_length);
                // fixup name
                if (module.StartsWith("%(Platform)"))
                    module = "pc" + module.Substring(11);

                // then correct the path to be a real path

                module = halo_infinite_path + "deploy\\" + module;
                result.Add(new module_structs.module(module));

                current_offset += string_length;
            }

            return result;
        }
    }
    public static class module_structs{
        public class module{
            // we have to simulate a file structure with modules, so our hierarchy works
            public Dictionary<string, List<indexed_module_file>> file_groups = new();
            public struct indexed_module_file{
                public indexed_module_file(string _name, string _alias, unpacked_module_file _file, /*int source_index,*/ bool resource, int _man_index){
                    name = _name;
                    alias = _alias;
                    file = _file;
                    //source_file_header_index = source_index;
                    is_resource = resource;
                    manifest_index = _man_index;}
                public string name;
                public string alias;
                public unpacked_module_file file;
                //public int source_file_header_index;
                public bool is_resource; // i think we're supposed to use this to tell users whether they can open this or not?
                public int manifest_index; // used so we can determine if this file is a manifest file, and if so which one (0, 1 or 2)
            }

            public string module_file_path; // idk why we'd need to store this // ok i just found a use, we use this so we can reopen the module after recompiling
            public string module_name;

            FileStream module_reader; // so we can read from the module when calling tags
            long tagdata_base;

            // just so we can recompile
            public module_header module_info;
            public module(string module_path){
                module_file_path = module_path;
                // and then he said "it's module'n time"
                if (!File.Exists(module_file_path)) throw new Exception("failed to find specified module file"); // probably redundant

                // get short file name so we can use it for the api
                if     (module_file_path.Contains("\\any\\")) module_name = "any:";
                else if (module_file_path.Contains("\\ds\\")) module_name = "ds:";
                else if (module_file_path.Contains("\\pc\\")) module_name = "pc:";
                module_name += Path.GetFileName(module_file_path);

                module_reader = new FileStream(module_file_path, FileMode.Open, FileAccess.Read);

                // read module header
                module_info = read_and_convert_to<module_header>(module_header_size);

                // read module file headers
                module_file[] files = new module_file[module_info.FileCount];
                for (int i = 0; i < files.Length; i++)
                    files[i] = read_and_convert_to<module_file>(module_file_size);

                // read the string table
                byte[] string_table = new byte[module_info.StringsSize];
                module_reader.Read(string_table, 0, module_info.StringsSize);

                // read the resource indicies?
                int[] resource_table = new int[module_info.ResourceCount];
                for (int i = 0; i < resource_table.Length; i++)
                    resource_table[i] = read_and_convert_to<int>(4); // we should also fix this one too

                // read the data blocks
                block_header[] blocks = new block_header[module_info.BlockCount];
                for (int i = 0; i < blocks.Length; i++)
                    blocks[i] = read_and_convert_to<block_header>(block_header_size);

                // now to read the compressed data junk

                // align accordingly to 0x?????000 padding to read data
                tagdata_base = (module_reader.Position / 0x1000 + 1) * 0x1000;
                //module_reader.Seek(aligned_address, SeekOrigin.Begin);

                // then we need to map out our directory, so the tools 
                for (int i = 0; i < files.Length; i++){
                    module_file tag = files[i];
                    // we now need to convert this an upacked module file
                    // first get blocks
                    // then get resources
                    // also dont forget to paste out header in

                    if (tag.ParentIndex != -1) continue; // skip resource files as we process them while getting tag resources

                    // init group if it hasn't been already
                    string group = groupID_str(tag.ClassId);
                    if (!file_groups.ContainsKey(group))
                        file_groups.Add(group, new List<indexed_module_file>());
                    // get tagname and add to directory
                    string idname = tag.GlobalTagId.ToString("X8");
                    string tagname = get_shorttagname(tag.GlobalTagId);

                    // we actually want to go through and get all the resources here, instead of loading them as separate files
                    unpacked_module_file tag_unpacked = new(tag, blocks);
                    //unpack_module_file(tag_unpacked);

                    for (int resource_index = 0; resource_index < tag.ResourceCount; resource_index++){
                        module_file resource_tag = files[resource_table[tag.ResourceIndex + resource_index]];
                        unpacked_module_file resource_unpacked = new(resource_tag, blocks);
                        //unpack_module_file(resource_unpacked);
                        tag_unpacked.resources.Add(resource_unpacked);
                        file_groups[group].Add(new(idname + "_res_" + resource_index, tagname + "_res_" + resource_index, resource_unpacked, true, -1));
                    }

                    // manifest index 0
                    if (i == module_info.Manifest00_index)
                        file_groups[group].Add(new(idname, tagname, tag_unpacked, false, 0));
                    // manifest index 1
                    else if (i == module_info.Manifest01_index)
                        file_groups[group].Add(new(idname, tagname, tag_unpacked, false, 1));
                    // manifest index 2
                    else if (i == module_info.Manifest02_index)
                        file_groups[group].Add(new(idname, tagname, tag_unpacked, false, 2));
                    // else regular file
                    else file_groups[group].Add(new(idname, tagname, tag_unpacked, false, -1));
                    
                }
                // ok thats all, the tags have been read

                // debug testing compiling
                //module_compiler mod_comper = new(this);
                //mod_comper.compile();
            }
            private void unpack_module_file(unpacked_module_file unpacked){
                if (unpacked.blocks != null) return; // already unpacked
                unpacked.blocks = new();

                // now we need to load all the blocks, easy enough
                bool using_compression = (unpacked.header.Flags & flag_UseCompression) != 0; // pretty sure this is true if reading_seperate_blocks is also true, confirmation needed
                bool reading_separate_blocks = (unpacked.header.Flags & flag_UseBlocks) != 0;
                bool reading_raw_file = (unpacked.header.Flags & flag_UseRawfile) != 0;
                long data_Address = tagdata_base + unpacked.header.get_dataoffset();

                // test whether this tag is HD_1 (through flags & backup address offset) and match it with our imaginary value
                if ((unpacked.header.get_dataflags() & flag2_UseHd1) == 1 || data_Address >= module_reader.Length)
                    return; // skip adding data blocks basically // this only happens for bitmaps i think, so we shouldn't have any issues with this??

                if (reading_separate_blocks){
                    for (int b = 0; b < unpacked.module_blocks.Count; b++){
                        var bloc = unpacked.module_blocks[b];
                        byte[] block_bytes;
                        module_reader.Seek(data_Address + bloc.CompressedOffset, SeekOrigin.Begin);
                        // determine read size from whether data is compressed or not
                        if (bloc.Compressed == 1) block_bytes = new byte[bloc.CompressedSize];
                        else block_bytes = new byte[bloc.UncompressedSize];
                        // read data & push to tag data blocks
                        module_reader.Read(block_bytes, 0, block_bytes.Length);
                        unpacked.blocks.Add(new(((bloc.Compressed == 1) ? bloc.UncompressedSize : -1), bloc.UncompressedOffset, block_bytes));
                }}else {  // is the manifest thingo, aka raw file, read data based off compressed and uncompressed length
                    byte[] block_bytes;
                    module_reader.Seek(data_Address, SeekOrigin.Begin);
                    // determine read size from whether data is compressed or not
                    if (using_compression) block_bytes = new byte[unpacked.header.TotalCompressedSize];
                    else block_bytes = new byte[unpacked.header.TotalUncompressedSize];
                    // read data & push to tag data blocks
                    module_reader.Read(block_bytes, 0, block_bytes.Length);
                    unpacked.blocks.Add(new( ((using_compression)? unpacked.header.TotalUncompressedSize : -1), 0, block_bytes));
                }
                // resources will be handled outside of this, purely for less code
                return;
            }
            // this will not be called by any internal functions, but may be called by external functions if desired? to allow tools to keep mem usage low for big module files
            public void flush_module_file(unpacked_module_file unpacked){
                if (unpacked.blocks != null){
                    unpacked.blocks.Clear();
                    unpacked.blocks = null;
            }}
            // unpack files?
            public class unpacked_module_file{ // class so when editing properties for compiling, they'll stick back to the main module
                public unpacked_module_file(module_file _header, block_header[]? _blocks = null) {
                    header = _header;
                    module_blocks = new();
                    blocks = null; // we use this to determine whether the tag is currently unpacked or not
                    resources = new();
                    // load block headers straight in, so we can unpack on demand
                    if (_blocks != null)
                        for (int i = 0; i < header.BlockCount; i++)
                            module_blocks.Add(_blocks[header.BlockIndex + i]);
                }
                public module_file header;
                public List<block_header> module_blocks; // this is used so we dont have to load every single block to memory to view tags
                public List<packed_block>? blocks; // nullable so we can safely dispose & reload tags for memory purposes
                public List<unpacked_module_file> resources; // we shouldn't have a recursive structure, but it should work fine
                public bool has_been_edited = false; // used to determine whether a tag is eligable for repacking (& requiring repacked blocks)
            }
            public struct packed_block{
                public packed_block(int uncomp_size, int uncomp_offset, byte[] data){
                    uncompressed_size = uncomp_size;
                    uncompressed_offset = uncomp_offset;
                    bytes = data;}
                public int uncompressed_size; // we dont want to have to de/recompress every tag every time we pack a single tag, so store it as already compressed
                public int uncompressed_offset; // while we dont necessarily need this, its probably important to have
                public byte[] bytes;
            }

            public byte[] get_module_file_bytes(unpacked_module_file tag){
                byte[] decompressed_data = new byte[tag.header.TotalUncompressedSize];
                unpack_module_file(tag); // this makes sure that we unpack before getting bytes
                if (tag.blocks == null) throw new Exception("tag has no blocks, despite just unpacking");
                foreach (var block in tag.blocks){
                    // get byte array to copy from, decompress if needed
                    byte[] block_bytes;
                    if (block.uncompressed_size != -1) block_bytes = Oodle.Decompress(block.bytes, block.bytes.Length, block.uncompressed_size);
                    else block_bytes = block.bytes;
                    // paste into decompressed buffer
                    System.Buffer.BlockCopy(block_bytes, 0, decompressed_data, block.uncompressed_offset, block_bytes.Length);
                }
                return decompressed_data;
            }
            public List<byte[]> get_tag_resource_list(unpacked_module_file parent_tag){
                // get all resources & then read them into a list
                List<byte[]> output = new();
                for (int i = 0; i < parent_tag.resources.Count; i++)
                    output.Add(get_module_file_bytes(parent_tag.resources[i]));
                
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


            public class module_compiler{
                module target_module;
                public module_compiler(module active_module){
                    target_module = active_module;


                }
                public void pack_tag(byte[] tag_bytes, List<byte[]> resource_bytes, uint? tagID, ulong? assetID, uint? group, bool force_repack_only){
                    // we simply process this tag & break it up into X compressed blocks & either add those blocks to the target tag
                    // or add a brand new tag

                    // we might have to research exactly how the blocks are determined though
                    // min size for compression
                    // min/max block size
                    // how many blocks to make
                    // etc


                    // if tagID is null, we can fetch it from the tag bytes
                    if (tagID == null){
                        tag_context context = return_tag_file_contextdata(tag_bytes);
                        tagID = context.tagID;
                        assetID = context.new_assetID;
                        group = get_group_uint_from_string(context.tag_group); // what did we do to convert this again
                    }

                    // new comments
                    // first step is test whether this tag already exists in our list (via tagID)
                    foreach(var v in target_module.file_groups){
                        for (int i = 0; i < v.Value.Count; i++){
                            indexed_module_file index_file = v.Value[i];
                            if (index_file.file.header.GlobalTagId == tagID){
                                // repack contents
                                unpacked_module_file new_tag_content = pack_file(tag_bytes, index_file.file.header);

                                // update resources?

                                // plus create headers for any new ones?
                                // remove old resources from the file heirarchy
                                for (int file_index = 0; file_index < v.Value.Count; file_index++){
                                    foreach(var resource in index_file.file.resources){
                                        if (resource == v.Value[file_index].file){
                                            // we actually throw off the index of 'i' by removing resources, so we need to account for the new index of I
                                            if (file_index < i) i--; // and well if it equals I then we failed somewhere
                                            // then this file
                                            v.Value.RemoveAt(file_index);
                                            file_index--; // so we dont skip anything
                                            break; // if this index was a match, then the last index will not be
                                }}}

                                // repack resources
                                for (int resource_index = 0; resource_index < resource_bytes.Count; resource_index++) {
                                    // dont even bother using the old file headers for the resources, just generate some new ones
                                    module_file new_res_header = new();
                                    new_res_header.GlobalTagId = 0xffffffffu;
                                    new_res_header.ClassId = -1;
                                    new_res_header.AssetId = 0xffffffffffffffffu;
                                    unpacked_module_file new_resource_content = pack_file(resource_bytes[resource_index], new_res_header);

                                    new_tag_content.resources.Add(new_resource_content);
                                    v.Value.Add(new(index_file.name + "_res_" + resource_index, index_file.alias + "_res_" + resource_index, new_resource_content, true, -1));
                                }

                                index_file.file = new_tag_content;
                                v.Value[i] = index_file;
                                return;
                            }
                            // otherwise continue looking
                        }
                    }
                    if (force_repack_only) throw new Exception("tag does not exist in target module file!! (despite specifying that it has to exist)");
                    throw new Exception("importing new tags is currently unsupported!! (tags must be referenced in the runtime manifest or something, which requires further code stuff)");
                    /*// if no matches found then we assume we're creating a new tag header
                    // check to make sure we actually want to create a new one, else epic exception
                    if (assetID == null || group == null)
                        throw new Exception("couldn't find target id in module, and no extra details provided to generate new tag header!!");

                    module_file new_file_header = new();
                    new_file_header.GlobalTagId = tagID;
                    new_file_header.AssetId = (ulong)assetID;
                    new_file_header.ClassId = (int)group;
                    unpacked_module_file packed_file = pack_file(tag_bytes, new_file_header);

                    string target_folder = groupID_str((int)group);
                    if (!target_module.file_groups.ContainsKey(target_folder))
                        target_module.file_groups.Add(target_folder, new List<indexed_module_file>());
                    // get tagname and add to directory
                    string idname = tagID.ToString("X8");
                    string tagname = get_shorttagname(tagID);

                    // then append all resources
                    for (int resource_index = 0; resource_index < resource_bytes.Count; resource_index++){
                        module_file resource_tag = new();
                        resource_tag.GlobalTagId = 0xffffffffu;
                        resource_tag.ClassId = -1;
                        resource_tag.AssetId = 0xffffffffffffffffu;
                        unpacked_module_file packed_resource = pack_file(tag_bytes, new_file_header);

                        packed_file.resources.Add(packed_resource);
                        target_module.file_groups[target_folder].Add(new(idname + "_res_" + resource_index, tagname + "_res_" + resource_index, packed_resource, true, -1));
                    }
                    target_module.file_groups[target_folder].Add(new(idname, tagname, packed_file, false, -1));*/
                }
                struct tag_context {
                    public uint tagID;
                    public ulong new_assetID;
                    public string tag_group;
                }
                private tag_context return_tag_file_contextdata(byte[] file_bytes){
                    tag_context result = new();
                    // this is a bit complicated, but it should work fine
                    if (!file_bytes[0..4].SequenceEqual(tag_magic))
                        throw new Exception("provided file does not have a tagid because its not a tag!!");

                    tag_header header = KindaSafe_SuperCast<tag_header>(file_bytes, 0ul);
                    int offset = tag_header_size;
                    offset += header.DependencyCount * tag_dependency_size;
                    int data_block_offset = offset;
                    offset += header.DataBlockCount * data_block_size;

                    // we need to iterate through the tag structs to determine which one is the root, then we use that to determine which data block our root struct is at
                    // all extremely complicated to just read the tagid
                    for (int i = 0; i < header.TagStructCount; i++){
                        tag_def_structure current_struct = KindaSafe_SuperCast<tag_def_structure>(file_bytes, (ulong)(offset + (i * tag_def_structure_size)));
                        if(current_struct.Type == 0){
                            // then this is our root struct
                            if (current_struct.TargetIndex == -1 || current_struct.TargetIndex >= header.DataBlockCount)
                                throw new Exception("invalid struct target index! could not read tag file for ID correctly");
                            // get the data offset for this guy and then we can fetch the tagID at offset 0x8 from there
                            data_block datablock = KindaSafe_SuperCast<data_block>(file_bytes, (ulong)(data_block_offset + (current_struct.TargetIndex * data_block_size)));
                            uint tagID = KindaSafe_SuperCast<uint>(file_bytes, header.HeaderSize + datablock.Offset + 0x8);
                            result.tagID = tagID;
                            result.new_assetID = result.tagID << 32; // just do what it seems 343 does and shift the tagid into the upper 32 bits // TODO GENERATE RANDOM NUMBER IN LOWER 32 BITS
                            result.tag_group = get_tag_group_from_guid(current_struct.GUID_1.ToString("X16") + current_struct.GUID_2.ToString("X16"));
                            // fixup for that one tag
                            if (result.tag_group == "cmpsNAME_SIMILAR") result.tag_group = "cmps";
                            return result;
                        }
                    }
                    throw new Exception("could not find the root struct in the tag! this tag is probably broken");
                }
                private unpacked_module_file pack_file(byte[] file_bytes, module_file file_header){
                    
                    unpacked_module_file result = new(file_header);
                    result.blocks = new();
                    result.has_been_edited = true; // we definitely want to set that
                    // set all the placeholder values 
                    result.header.Unk_0x54 = -1;
                    result.header.DataOffset_and_flags = 0x0000FFFFFFFFFFFF; // do not give the option to set the hd1 flag, theres no point
                    // set all default/unknown values

                    // determine if this is a tag file
                    int offset = 0;
                    int comp_offset = 0;
                    bool is_using_compression = false;
                    if (file_bytes[0..4].SequenceEqual(tag_magic)){
                        // then we need to break this up into 4 categories of blocks
                        tag_header header = KindaSafe_SuperCast<tag_header>(file_bytes, 0ul);
                        is_using_compression |= pack_chunks(file_bytes, result.blocks, (int)header.HeaderSize, ref offset, ref comp_offset);
                        is_using_compression |= pack_chunks(file_bytes, result.blocks, (int)header.DataSize, ref offset, ref comp_offset);
                        is_using_compression |= pack_chunks(file_bytes, result.blocks, (int)header.ResourceDataSize, ref offset, ref comp_offset);
                        is_using_compression |= pack_chunks(file_bytes, result.blocks, (int)header.ActualResoureDataSize, ref offset, ref comp_offset);
                        // setup header data accordingly
                        result.header.DataOffset_and_flags |= flag_UseBlocks << 48;
                        // partition sizes
                        result.header.UncompressedHeaderSize = (int)header.HeaderSize;
                        result.header.UncompressedTagDataSize = (int)header.DataSize;
                        result.header.UncompressedResourceDataSize = (int)header.ResourceDataSize;
                        result.header.UncompressedActualResourceDataSize = (int)header.ActualResoureDataSize;
                        // partition alignments
                        result.header.HeaderAlignment = header.HeaderAlignment;
                        result.header.TagDataAlightment = header.TagDataAlightment;
                        result.header.ResourceDataAligment = header.ResourceDataAligment;
                        result.header.ActualResourceDataAligment = header.ActualResourceDataAligment;
                    // otherwise the file is a single chunk (or a few if large)
                    }else{
                        is_using_compression |= pack_chunks(file_bytes, result.blocks, file_bytes.Length, ref offset, ref comp_offset);
                        // set raw file flag
                        result.header.Flags |= flag_UseRawfile;
                        // non-tag files dont need any data header blocks if they only have 1
                        // NOTE: this doesn't matter as the code in the packer will just tell it to use blocks anyway
                        if (result.blocks.Count > 1)
                            result.header.Flags |= flag_UseBlocks;
                    }
                    // update file sizes
                    result.header.TotalUncompressedSize = offset;
                    result.header.TotalCompressedSize = comp_offset;
                    // check compression flag if the data was compressed
                    if (is_using_compression) result.header.Flags |= flag_UseCompression;
                    return result;
                }
                // returns whether the chunks were compressed
                private bool pack_chunks(byte[] source, List<packed_block> blocks, int remaining_length, ref int offset, ref int compressed_offset){
                    bool is_compressed = false;
                    // loop to process block into chunks with a max size of 0x100000
                    while (remaining_length > 0){
                        int chunk_length = remaining_length;
                        if (chunk_length > 0x100000)
                            chunk_length = 0x100000;
                        remaining_length -= 0x100000; // detract size that we're processing
                        // process & append current chunk
                        byte[] current_chunk = source[offset..(offset+ chunk_length)];
                        packed_block packed_chunk = new();
                        packed_chunk.uncompressed_offset = offset;
                        if (current_chunk.Length > 256){ // if chunk meets compression criteria
                            packed_chunk.uncompressed_size = current_chunk.Length;
                            packed_chunk.bytes = Oodle.Compress(current_chunk, OodleFormat.Kraken, OodleCompressionLevel.Normal);
                            is_compressed = true;
                        }else{ // do not compress
                            packed_chunk.uncompressed_size = -1;
                            packed_chunk.bytes = current_chunk;
                        }
                        blocks.Add(packed_chunk);
                        offset += chunk_length;
                        compressed_offset += packed_chunk.bytes.Length;
                    }
                    return is_compressed;
                }
                public int compile(string output_path){ // returns changed tags count?
                    //target_module.module_reader.Dispose(); // close read handle as we are about to modify the file
                    if (output_path == target_module.module_file_path)
                        throw new Exception("cannot overwrite original module file");

                    // read module header
                    module_header module_info = target_module.module_info;

                    // we dont really have to do this, but we will do it anyway to better break down the process
                    //unpacked_module_file unk_manifest_file_0; // present in some modules
                    //unpacked_module_file manifest_file_1;     // present in all modules
                    //unpacked_module_file unk_manifest_file_2; // not yet found
                    List<unpacked_module_file> files = new();
                    List<unpacked_module_file> resources = new();

                    // first we want to sort our tags into manageable groups (manifest, tags, resources)
                    // we'll process resources into our list here
                    foreach (var dir in target_module.file_groups){
                        for (int i = 0; i < dir.Value.Count; i++){
                            indexed_module_file file = dir.Value[i];

                            if (file.is_resource) continue; // we are not processing resources as files, we'll process them directly from tag's resource blocks
                            
                            // process resource table data
                            file.file.header.ResourceCount = (uint)file.file.resources.Count;
                            file.file.header.ResourceIndex = (uint)resources.Count;
                            for (int r = 0; r < file.file.resources.Count; r++){
                                unpacked_module_file resource = file.file.resources[r];
                                // update parent index
                                resource.header.ParentIndex = files.Count;
                                resources.Add(resource);
                                if (resource.resources.Count > 0)
                                    throw new Exception("resource files are currently not allowed to own sub resources!!");
                            }
                            // write index to header if this is a manifest file
                            if (file.manifest_index == 0)      module_info.Manifest00_index = files.Count;
                            else if (file.manifest_index == 1) module_info.Manifest01_index = files.Count;
                            else if (file.manifest_index == 2) module_info.Manifest02_index = files.Count;
                            // manifest files also go into the file queue // 343 typically puts manifest files at the beginning, but it shouldn't matter where they go
                            files.Add(file.file);
                        }
                    }
                    // now that we know how many regular files there are, we can build resource tables by just adding regular file count to each resource file index
                    List<int> resource_table = new();
                    // assume resource files DO NOT have resources
                    foreach (var file in files)
                        for (int r = 0; r < file.resources.Count; r++)
                            resource_table.Add((int)file.header.ResourceIndex + r + files.Count);

                    module_info.ResourceIndex = files.Count; // this gets the index of the first resource?
                    files.AddRange(resources); // dump resources into files list so we dont have to repeat any steps invdividually for resources

                    ulong file_bytes = 0;

                    // we're not even going to bother putting code for writing the string table, screw compatibility

                    List<block_header> header_blocks = new();
                    List<module_file> file_headers = new(); // we have to have a separate list for headers, as we cannot assign the edited versions until we finish compiling
                    // else we'd mess up the offsets when we try to unpack tags to repack them without recompiling them
                    int changed_tags = 0;
                    // now fill out the details for the blocks
                    for (int i = 0; i < files.Count; i++) {
                        unpacked_module_file file = files[i];
                        module_file file_header = file.header;
                        file_header.BlockIndex = (uint)header_blocks.Count;
                        bool ishd1 = ((file_header.get_dataflags() & flag2_UseHd1) == 1);
                        // check to see if this is a hd1 resource, if so then set the addres to -1 basically & do not allocate any room
                        if (ishd1) file_header.DataOffset_and_flags = (file_header.DataOffset_and_flags & 0x00ff000000000000) | 0x0000FFFFFFFFFFFF;
                        // otherwise just read our regular offset
                        else file_header.DataOffset_and_flags = (file_header.DataOffset_and_flags & 0x00ff000000000000) | file_bytes;

                        // clean up the unknown junk things that we dont know do anything
                        file_header.Unk_0x00 = 0;
                        file_header.Unk_0x54 = -1;
                        file_header.AssetChecksum = 0ul;

                        if (file.blocks == null || file.has_been_edited == false){ // block has no new data or hasn't been marked to repack, just toss their original datablocks back in
                            if (file.module_blocks.Count > 0){
                                foreach (block_header block in file.module_blocks){
                                    header_blocks.Add(block);
                                    if (!ishd1) file_bytes += (ulong)block.CompressedSize;
                            // its possible for the tag to have no blocks, but just have a data offset and compressed size, so we need to take that into account
                            }} else if (!ishd1) file_bytes += (ulong)file.header.TotalCompressedSize;
                        }else{ // otherwise we want to compile the updated data for this block
                            file_header.BlockCount = (ushort)file.blocks.Count;
                            // NOTE: this will overide resources who intentionally have no blocks allocated
                            // so to play it safe, we will set the using blocks flag just incase, this is not the proper solution however.
                            file_header.Flags |= flag_UseBlocks;
                            changed_tags++;

                            int compressed_offset = 0;
                            int uncompressed_offset = 0;
                            // we also want to compile the new blocks for this guy, so we can then read this file from disk after
                            file.module_blocks.Clear();
                            for (int b = 0; b < file.blocks.Count; b++){
                                packed_block data_block = file.blocks[b];
                                // yeah just create the block header as well, while we're at it
                                block_header current_header = new();
                                current_header.Compressed = (data_block.uncompressed_size == -1)? 0 : 1; // why cant we cast bool to int??????
                                if (current_header.Compressed == 1)
                                    current_header.UncompressedSize = data_block.uncompressed_size;
                                else current_header.UncompressedSize = data_block.bytes.Length;
                                current_header.CompressedSize = data_block.bytes.Length;
                                current_header.CompressedOffset = compressed_offset;
                                current_header.UncompressedOffset = uncompressed_offset;
                                // then increment offset
                                compressed_offset += current_header.CompressedSize;
                                uncompressed_offset += current_header.UncompressedSize;
                                // add to lists
                                header_blocks.Add(current_header);
                                file.module_blocks.Add(current_header);
                            }
                            if (!ishd1) file_bytes += (ulong)compressed_offset; // do not allocate bytes for hd1 tagdata, as we're just pretending it doesn't exist
                        }
                        // then add the updated header to the list
                        file_headers.Add(file_header);
                    }
                    if (file_headers.Count != files.Count) throw new Exception("somehow our file header list count does not match our file list count!!");

                    // lastly we need to update the module header to match the new information
                    module_info.BlockCount = header_blocks.Count;
                    module_info.FileCount = files.Count;
                    module_info.ResourceCount = resource_table.Count;
                    module_info.StringsSize = 0;
                    module_info.Checksum = 0; // just because

                    // then start writing stuff into the file
                    // we should use a filestream because we are not going to use write all bytes for this one
                    using (FileStream fsStream = new FileStream(output_path, FileMode.Create))
                    using (BinaryWriter writer = new BinaryWriter(fsStream, Encoding.UTF8)){
                        // write header
                        writer.Write(StructToBytes(module_info));
                        // write file headers
                        foreach (module_file header in file_headers)
                            writer.Write(StructToBytes(header));
                        // write resource table
                        foreach (int resource_index in resource_table)
                            writer.Write(resource_index);
                        // write data blocks
                        foreach (block_header block in header_blocks)
                            writer.Write(StructToBytes(block));

                        // update current module tagdata address (so it wont break if we continue using this module without reopening the tool)
                        target_module.tagdata_base = (writer.BaseStream.Position / 0x1000 + 1) * 0x1000;
                        // we then have to calculate the padding (to get to the next 0x?????000 address?)
                        long padding_required = 0x1000 - (writer.BaseStream.Position % 0x1000);
                        writer.Write(new byte[padding_required]);

                        // now write all the tagdata
                        // as long as we use the same algorithm we should not have any issues with aligning the data
                        for (int i = 0; i < files.Count; i++){
                            unpacked_module_file file = files[i];
                            module_file updated_header = file_headers[i];
                            target_module.unpack_module_file(file);
                            if (file.blocks == null) throw new Exception("failed to unpack tag's blocks!");
                            // only write bytes if its not a hd1 file
                            if ((file.header.get_dataflags() & flag2_UseHd1) == 0){

                                for (int b = 0; b < file.blocks.Count; b++){
                                    // do some error checking to make sure the code worked exactly how we thought it would
                                    if (updated_header.BlockCount > 0){
                                        if ((writer.BaseStream.Position - target_module.tagdata_base) != updated_header.get_dataoffset() + header_blocks[(int)updated_header.BlockIndex + b].CompressedOffset)
                                            throw new Exception("offset fail! bad offset when writing tag data!!!");
                                        if (file.blocks[b].bytes.Length != header_blocks[(int)updated_header.BlockIndex + b].CompressedSize)
                                            throw new Exception("incorrect block size when writing tag data!");
                                    // different error checking depending on whether this tag has module header blocks
                                    } else {
                                        if ((writer.BaseStream.Position - target_module.tagdata_base) != updated_header.get_dataoffset())
                                            throw new Exception("offset fail! bad offset when writing tag data!!!");
                                    }

                                    writer.Write(file.blocks[b].bytes);
                                }
                            }
                            // only cleanup resources if we dont have them open for editing
                            if (file.has_been_edited == false) target_module.flush_module_file(file); // cleanup resources so we dont buildup a huge amount of RAM
                            // then we can apply the updated header, as we've officially finished reading this tag from the original file
                            file.header = updated_header;
                        }



                    }
                    // pass updated module info back to our module, so we can continue using it
                    target_module.module_info = module_info;
                    // reopen module now that we've finished editing it
                    target_module.module_reader = new FileStream(target_module.module_file_path, FileMode.Open, FileAccess.Read);

                    return changed_tags;
                }


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
            [FieldOffset(0x04)] public int Version;        //  48 flight1, 51 flight2 & retail // idk what version we're up to now
            [FieldOffset(0x08)] public ulong ModuleId;       //  randomized between modules, algo unknown
            [FieldOffset(0x10)] public int FileCount;      //  the total number of tags contained by the module

            [FieldOffset(0x14)] public int Manifest00_index;   //  not in most modules
            [FieldOffset(0x18)] public int Manifest01_index;   //  present in most modules 
            [FieldOffset(0x1C)] public int Manifest02_index;   //  so not not noticed to be present in any modules

            [FieldOffset(0x20)] public int ResourceIndex;   //  "Index of the first resource entry (numFiles - numResources)"
            [FieldOffset(0x24)] public int StringsSize;     //  total size (in bytes) of the strings table
            [FieldOffset(0x28)] public int ResourceCount;   //  number of resource files
            [FieldOffset(0x2C)] public int BlockCount;      //  number of data blocks

            [FieldOffset(0x30)] public ulong BuildVersion;    // this should be the same between each module
            [FieldOffset(0x38)] public ulong Checksum;        // "Murmur3_x64_128 of the header (set this field to 0 first), file list, resource list, and block list"
            // new with infinite
            [FieldOffset(0x40)] public int Unk_0x040;       //  0
            [FieldOffset(0x44)] public int Unk_0x044;       //  0
            [FieldOffset(0x48)] public int Unk_0x048;       //  2 // i feel like this must be an alignment thing?
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
            [FieldOffset(0x00)] public byte Unk_0x00;     //  
            [FieldOffset(0x01)] public byte Flags;          // refer to flag bits below this struct
            [FieldOffset(0x02)] public ushort BlockCount;     // "The number of blocks that make up the file. Only valid if the HasBlocks flag is set"
            [FieldOffset(0x04)] public uint BlockIndex;     // "The index of the first block in the file. Only valid if the HasBlocks flag is set"
            [FieldOffset(0x08)] public uint ResourceIndex;  // "Index of the first resource in the module's resource list that this file owns"

            [FieldOffset(0x0C)] public int ClassId;        // this is the tag group, should be a string right?

            [FieldOffset(0x10)] public ulong DataOffset_and_flags;     // for now just read as a long // wow we were not infact reading this a long
            //[FieldOffset(0x14)] public uint    Unk_0x14;       // we will now need to double check each file to make sure if this number is ever anything // its used in the very big files

            [FieldOffset(0x18)] public int TotalCompressedSize;    // "The total size of compressed data."
            [FieldOffset(0x1C)] public int TotalUncompressedSize;  // "The total size of the data after it is uncompressed. If this is 0, then the file is empty."

            [FieldOffset(0x20)] public uint GlobalTagId;   // this is the murmur3 hash; autogenerate from tag path

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
            [FieldOffset(0x54)] public int Unk_0x54;       // 
        }
        // 'Flags' // 
        const byte flag_UseCompression = 0b00000001; // Uses Compression
        const byte flag_UseBlocks      = 0b00000010; // has blocks, which means to read the data across several data blocks, otherwise read straight from data offset
        const byte flag_UseRawfile     = 0b00000100; // is a raw file, meaning it has no tag header
        // data offset flags
        const byte flag2_UseHd1 = 0b00000001; // if this is checked, then its likely that the tag resides in the hd1 file (if that exists)

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

    // load stringID's
    // load tagnames
    public static class tag_structs{
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
        static Dictionary<uint, string> stringIDs = new Dictionary<uint, string>();
        static Dictionary<uint, string> tagnames = new Dictionary<uint, string>();
        const string stringIDs_path = "files\\outtest10.txt";
        const string tagnames_path = "files\\tagnames.txt";
        static string? plugin_path = "C:\\Users\\Joe bingle\\Downloads\\plugins";
        public static void init_strings(){
            // generate stringID's list
            var lines = File.ReadLines(stringIDs_path);
            foreach (var line in lines){
                string[] parts = line.Split(":");
                uint stringid = Convert.ToUInt32(parts[0], 16);
                stringIDs[stringid] = parts[1];
            }
            // generate tagnames list
            lines = File.ReadLines(tagnames_path);
            foreach (var line in lines){
                string[] parts = line.Split(" : ");
                uint tagid = Convert.ToUInt32(parts[0], 16);
                tagnames[tagid] = parts[1];
            }
        }
        public static void init_plugins(){

        }
        public static string get_shorttagname(uint tagid) { 
            if (tagnames.TryGetValue(reverse_uint(tagid), out string? tagname))
            {
                // old method, gets single phrase name
                //// get extension to test whether we need to get the second best name
                //string ext = tagname.Split(".").Last();
                //switch (ext){
                //    case "runtime_geo":
                //    case "static_collision":
                //    case "rtmp":
                //    case "model":
                //        // we can only do the second best name if it has at least a single '\' (which would be odd if it didn't)
                //        string[] paths = tagname.Split("\\");
                //        if (paths.Length >= 2) return paths[paths.Length - 2].Split(".").First();
                //        else break;
                //}


                //return tagname.Split("\\").Last().Split(".").First();

                // new method, pulls file name & parent directory
                string[] paths = tagname.Split("\\");
                string result = "";
                if (paths.Length > 1)
                    result += paths[paths.Length - 2];
                return result + paths.Last().Split(".").First();
            }
            else return reverse_uint(tagid).ToString("X8");
            // we want to get rid of the dumb names for dumb tags, so filter out basic types like
        }
        public static string get_tagname(uint tagid){
            if (tagnames.TryGetValue(reverse_uint(tagid), out string? tagname))
                return tagname;
            else return reverse_uint(tagid).ToString("X8");
        }
        // TODO: separate this into the init plugins function
        public static string get_tag_group_from_guid(string guid){
            // init tag GUID's & figure out what tag this is
            if (GUIDs_to_groups == null){
                GUIDs_to_groups = new();
                using (StreamReader reader = File.OpenText(plugin_path + "\\GUIDs.txt")){
                    while (!reader.EndOfStream) {
                        string[] line = reader.ReadLine().Split(":");
                        GUIDs_to_groups.Add(line[1], line[0]); // GUID -> group
            }}}
            return GUIDs_to_groups[guid];
        }
        public static string get_stringid(uint stringid){
            if (stringIDs.TryGetValue(reverse_uint(stringid), out string? stringname))
                return stringname;
            else return reverse_uint(stringid).ToString("X8");
        }
        public static uint reverse_uint(uint input){
            uint output = ((input & 0xff) << 24)
                        | ((input & 0xff00) << 8)
                        | ((input & 0xff0000) >> 8)
                        | ((input & 0xff000000) >> 24);
            return output;
        }
        public static uint get_group_uint_from_string(string group){
            if (group.Length != 4) throw new Exception("group should be explicitly 4 characters!!");
            byte[] bytes = Encoding.ASCII.GetBytes(group);
            return reverse_uint(BitConverter.ToUInt32(bytes));
        }

        static Dictionary<string, string>? GUIDs_to_groups = null;
        public static byte[] tag_magic = new byte[4] { 0x75, 0x63, 0x73, 0x68 };
        // currently having this as a class, so that we can just copy pointers to this structure for effiency
        public class tag {
            public tag(List<KeyValuePair<byte[], bool>>? resources, XmlNode? _reference_root = null, XmlDocument? _reference_xml = null)
            {
                resource_list = resources;
                reference_root = _reference_root;
                reference_xml = _reference_xml;

                if (resources == null) return; // if no resources then we dont need to iterate, this will mostly be for the non chunked resources i think
                // iterate through all resource files & ensure they are all the same type, or throw error
                for (int i = 0; i < resources.Count; i++)
                {
                    // note that the array sets the bool as true when the resource is a standalone tag, AKA not chunked
                    // so we have to inverse that boolen when doing comparisons here, as we want to know whether its chunked (true)
                    bool is_chunked = !resources[i].Value;

                    // first index sets what resource type we're using, the next ones
                    if (i == 0) are_resources_chunked = is_chunked;
                    else if (are_resources_chunked != is_chunked)
                        throw new Exception("some resource files were not of matching type (chunked/standalone)!! this probably shouldn't happen ever");

                }
            }
            // load tag outputs
            public bool Initialized = false;
            public tagdata_struct? root = null;
            public byte[] unmarked_header_data;
            public XmlNode? reference_root;
            //
            //
            XmlDocument? reference_xml;
            // resource path, is whole file (aka non-chunked)
            public List<KeyValuePair<byte[], bool>> resource_list;
            bool are_resources_chunked = false; // default as using non-chunked resources, as most tags that use non-chunked have a possibility to have no resources
            int processed_resource_index = 0; // we use this to keep track of which files we've opened

            private T read_and_convert_to<T>(int read_length, MemoryStream tag_reader) {
                byte[] bytes = new byte[read_length];
                tag_reader.Read(bytes, 0, read_length);
                return KindaSafe_SuperCast<T>(bytes);
            }
            private T[] struct_array_assign_bytes<T>(int count, int struct_size, MemoryStream tag_reader) {
                T[] output = new T[count];
                for (int i = 0; i < count; i++)
                    output[i] = read_and_convert_to<T>(struct_size, tag_reader);
                return output;
            }
            private byte[] read_at(long position, int length, MemoryStream tag_reader) {
                byte[] bytes = new byte[length];
                tag_reader.Seek(position, SeekOrigin.Begin);
                tag_reader.Read(bytes, 0, length);
                return bytes;
            }
            //MemoryStream? tag_reader; // to be cleaned up after loading
            public bool Load_tag_file(byte[] tag_bytes, string target_guid = "") {
                //if (!File.Exists(tag_path)) return false; // failed via invalid directory
                using (MemoryStream tag_reader = new MemoryStream(tag_bytes)){
                    // read the first 4 bytes to make sure this is a tag file
                    byte[] header_test = tag_bytes[0..4];

                    if (!header_test.SequenceEqual(tag_magic)){
                        return false; // failed due to not a tag file
                    }
                    // ok begin parsing the tag
                    tag_reader.Seek(0, SeekOrigin.Begin); // reset position
                    header = read_and_convert_to<tag_header>(tag_header_size, tag_reader);
                    // read tag dependencies
                    dependencies = struct_array_assign_bytes<tag_dependency>(header.DependencyCount, tag_dependency_size, tag_reader);
                    // read tag data blocks
                    data_blocks = struct_array_assign_bytes<data_block>(header.DataBlockCount, data_block_size, tag_reader);
                    foreach (var var in data_blocks){
                        if (var.Section == 0){
                            // debug here to figure out why this stuff is put here?
                        }
                    }
                    // read tag ref structures
                    tag_structs = struct_array_assign_bytes<tag_def_structure>(header.TagStructCount, tag_def_structure_size, tag_reader);
                    // process that array into a faster dictionary
                    struct_links = new Dictionary<uint, uint>[tag_structs.Length];
                    // initialize each element in the array
                    for (int i = 0; i < tag_structs.Length; i++) struct_links[i] = new();
                    // write an array of data block's parent structs, so that we may find a structs parent through the block it resides inside of
                    int[] block_to_struct_links = new int[data_blocks.Length];

                    for (uint i = 0; i < block_to_struct_links.Length; i++) block_to_struct_links[i] = -1; // placeholdewr until we hfigure out how to do this normally


                    for (uint i = 0; i < tag_structs.Length; i++){
                        if (tag_structs[i].TargetIndex == -1 || tag_structs[i].Type == 2 || tag_structs[i].Type == 4) continue; // either a null struct or is a resource struct
                        // FU joe halo, why are some of your tags poorly formatted, where tags structs can have the same target index as their field index
                        if (block_to_struct_links[tag_structs[i].TargetIndex] != -1) continue; // FAILSAFE

                        block_to_struct_links[tag_structs[i].TargetIndex] = (int)i;
                    }
                    // assign the child structs links, based on their parent field block
                    for (uint i = 0; i < tag_structs.Length; i++){
                        if (tag_structs[i].Type == 0 || tag_structs[i].Type == 4) continue; // the root struct has no parent, AND TYPE 4'S OVERLAP WITH OTHER PARAMS??
                        var ven = block_to_struct_links[tag_structs[i].FieldBlock];
                        var ben = struct_links[ven];
                        ben.Add(tag_structs[i].FieldOffset, i);
                    }
                    // read tag data references?
                    data_references = struct_array_assign_bytes<data_reference>(header.DataReferenceCount, data_reference_size, tag_reader);
                    // now write the links array, so that we can easily find out which struct owns each data
                    data_links = new Dictionary<uint, uint>[tag_structs.Length];
                    // initialize each element in the array
                    for (uint i = 0; i < tag_structs.Length; i++) data_links[i] = new();
                    for (uint i = 0; i < data_references.Length; i++)
                        data_links[data_references[i].ParentStructIndex].Add(data_references[i].FieldOffset, i);
                    

                    // TAG STRUCTS ALSO REFER TO RESOURCE FILE STRUCTS!!!!!!!!!!!!!

                    // read tag tag fixup references 
                    tag_fixup_references = struct_array_assign_bytes<tag_fixup_reference>(header.TagReferenceCount, tag_fixup_reference_size, tag_reader);
                    // assign the string table bytes, wow this is not convienent at all lol // more or less now more convenient
                    //if (header.StringTableSize > 0) { // this is no longer used !!!! despite what the few tags are trying to tell us
                    //    //local_string_table = new byte[header.StringTableSize];
                    //    local_string_table = new byte[header.StringTableSize];
                    //    tag_reader.Read(local_string_table, 0, (int)header.StringTableSize);
                    //}
                    // read the zoneset header
                    long pre_zoneset_position = tag_reader.Position;
                    zoneset_info = read_and_convert_to<zoneset_header>(zoneset_header_size, tag_reader);
                    if (header.ZoneSetDataSize > zoneset_header_size){
                        // then we must read the children or DIE
                        // ZONESETS SEEM TO BE NULLED (BD'd) OUT??? // i have no idea where we saw this
                        // read all the zoneset instances
                        zonesets = new zoneset_instance[zoneset_info.ZonesetCount];
                        // its literally not possible for that to be a null reference, we just set it above
                        for (int m = 0; m < zonesets.Length; m++) {
                            // read the header
                            zonesets[m].header = read_and_convert_to<zoneset_instance_header>(zoneset_instance_header_size, tag_reader);
                            // read the regular zoneset tags
                            zonesets[m].zonset_tags = struct_array_assign_bytes<zoneset_tag>(zonesets[m].header.TagCount, zoneset_tag_size, tag_reader);
                            // read the zoneset footer tags (whatever they are?)
                            zonesets[m].zonset_footer_tags = struct_array_assign_bytes<zoneset_tag>(zonesets[m].header.FooterCount, zoneset_tag_size, tag_reader);
                            // read the parents
                            zonesets[m].zonset_parents = struct_array_assign_bytes<int>(zonesets[m].header.ParentCount, 4, tag_reader);
                        }
                        // we forgot to read the zoneset header bytes?

                    }
                    // verify that we read the entire zoneset header
                    if (tag_reader.Position - pre_zoneset_position != header.ZoneSetDataSize){
                        throw new Exception("failed to read the zonest data correctly!");
                    }


                    // end of header, double check to make sure we read it all correctly // APPARENTLY THERES A LOT OF CASES WITH DATA THAT WE DONT READ !!!!!!!!!!!!!! FU BUNGIE
                    if (tag_reader.Position != header.HeaderSize){
                        if (tag_reader.Position > header.HeaderSize) throw new Exception("tag reader read more bytes in the header than supposed to!");

                        int extra_junk_size = (int)(header.HeaderSize - tag_reader.Position);
                        unmarked_header_data = new byte[extra_junk_size];
                        tag_reader.Read(unmarked_header_data, 0, extra_junk_size);
                    }
                    else unmarked_header_data = new byte[0];

                    // TODO: we need to cast this below read as the rest of the structures, as we're reading this section twice??
                    // read tag header data (so we can access any tag data that gets stored at the end)
                    header_data = read_at(0, (int)header.HeaderSize, tag_reader);
                    // read tag data
                    if (header.DataSize > 0) tag_data = read_at(header.HeaderSize, (int)header.DataSize, tag_reader);
                    // read resource data
                    if (header.ResourceDataSize > 0) tag_resource = read_at(header.HeaderSize + header.DataSize, (int)header.ResourceDataSize, tag_reader);
                    // read actual resource data
                    if (header.ActualResoureDataSize > 0) actual_tag_resource = read_at(header.HeaderSize + header.DataSize + header.ResourceDataSize, (int)header.ActualResoureDataSize, tag_reader);
                }

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

                            string tagus_groupus = get_tag_group_from_guid(root_GUID);

                            // setup reference plugin file
                            reference_xml = new XmlDocument();
                            reference_xml.Load(plugin_path + "\\" + tagus_groupus + ".xml");
                            reference_root = reference_xml.SelectSingleNode("root");
                            // do a runtime edit of the xmls to notate which structs have the unknown values set, so we can set them when recompiling
                            foreach (var struc in tag_structs){
                                if (struc.Type == 0 || struc.Type == 1 || struc.Type == 4){
                                    // error check
                                    if (struc.Unk_0x12 > 1){
                                        throw new Exception("unexpected unk type for non-chunked resource");
                                    // if unk is set to 1, then write this to the xml
                                    } else if (struc.Unk_0x12 == 1){
                                        string struc_guid = struc.GUID_1.ToString("X16") + struc.GUID_2.ToString("X16");
                                        XmlNode? target_node = reference_root.SelectSingleNode("_" + struc_guid);
                                        if (target_node == null) throw new Exception("failed to match GUID of important struct up with xml plugin struct");
                                        // now write the runtime attribute
                                        XmlAttribute attr = reference_xml.CreateAttribute("UNK0x12");
                                        target_node.Attributes.Append(attr);
                                }} else if (struc.Type == 2) {
                                    //if (struc.Unk_0x12 != 0) throw new Exception("unexpected unk type for non-chunked resource struct");
                                } else if (struc.Type == 3) {
                                    //if (struc.Unk_0x12 != 1) throw new Exception("unexpected unk type for chunked resource struct");
                                } else {
                                    throw new Exception("unexpected struct type!! (this needs to be investigated)");
                                }
                            }



                            root = process_highlevel_struct(i, root_GUID);
                            break;
                }}}else{ // is a subtag
                    // reference_xml = inherited; // we actually forgot to inherit this
                    // reference_root = inherited;
                    for (uint i = 0; i < tag_structs.Length; i++){
                        if (tag_structs[i].Type == 0){
                            root = process_highlevel_struct(i, target_guid);
                            break;
                }}}

                

                Initialized = true;
                return true;
                
            }
            public class tagdata_struct{
                // GUID needed, as we'll use that to figure out which struct to reference when interpretting the byte array
                public string GUID;
                public List<thing> blocks = new();
            }
            // do we even need this, or do we just want to read the data straight from the tagreference structure inlined into the tagdata???
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

                XmlNode currentStruct = reference_root.SelectSingleNode("_" + GUID);

                // we need to test whether this guid is correct or not // i think something is broken on 343's end and they assign the wrong guid's to some, so we have to account for that
                string packed_GUID = tag_structs[struct_index].GUID_1.ToString("X16") + tag_structs[struct_index].GUID_2.ToString("X16");
                if (packed_GUID != GUID){
                    // then we have to write the target packed guid to the xml node so we can fetch that again later
                    XmlAttribute attr = reference_xml.CreateAttribute("Override");
                    attr.Value = packed_GUID;
                    currentStruct.Attributes.Append(attr);
                }

                // test if this struct is null, nothing needs to be read if it is
                if (tag_structs[struct_index].TargetIndex == -1) return output_struct;



                int referenced_array_size = Convert.ToInt32(currentStruct.Attributes["Size"].Value, 16);

                data_block struct_file_offset = data_blocks[tag_structs[struct_index].TargetIndex];
                byte[] tag_datas = return_referenced_byte_segment(struct_file_offset.Section);

                for (int i = 0; i < block_count; i++)
                {
                    var test = new thing();
                    int offset = (int)struct_file_offset.Offset + (referenced_array_size * i);
                    if (offset + referenced_array_size > tag_datas.Length)
                        throw new Exception("tagblock referenced data outside of assigned data block");
                    test.tag_data = tag_datas.Skip(offset).Take(referenced_array_size).ToArray();
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
                                    append_to.tag_resource_refs.Add(tagblock_constant_offset, data_bytes);
                                }
                                else append_to.tag_resource_refs.Add(tagblock_constant_offset, new byte[0]);
                            }
                            break;
                        case "_43": // _field_resource - 16byte
                            // hmm, this section works basically exactly the same as tagblocks, except these refer to files outside of this file
                            {

                                uint next_struct_index = struct_links[struct_index][(uint)data_block_offset];
                                int resource_index = tag_structs[next_struct_index].TargetIndex;

                                string struct_guid = currentParam.Attributes["GUID"].Value;
                                ulong param_offset = data_block_offset + data_blocks[tag_structs[struct_index].TargetIndex].Offset;
                                int resource_type = read_int_from_array_at(data_blocks[tag_structs[struct_index].TargetIndex].Section, (int)(param_offset + 12));
                                int debug = data_blocks[tag_structs[struct_index].TargetIndex].Section;
                                if (debug != 1)
                                    throw new Exception("Debug!!!");

                                if (resource_type == 0)  // external tag resource type // UNKOWN HOW TO READ AS OF RIGHT NOW
                                {
                                    //uint resource_struct_index = struct_links[struct_index][(uint)data_block_offset];
                                    //tag_def_structure test = tag_structs[resource_struct_index];
                                    //data_block data_data = data_blocks[test.TargetIndex];
                                    //byte[] data_bytes = return_referenced_byte_segment(data_data.Section).Skip((int)data_data.Offset).Take((int)data_data.Size).ToArray();
                                    //string bitles = BitConverter.ToString(data_bytes).Replace('-', ' ');
                                    if (resource_index == -1)
                                    { // empty resource reference
                                        // do not append reference if it doesn't exist???
                                        // that was dumb, reverted
                                        append_to.resource_file_refs.Add(tagblock_constant_offset, null);
                                        continue;
                                    }
                                    if (processed_resource_index >= resource_list.Count)
                                        throw new Exception("indexed bad resource index");

                                    tag child_tag = new(null, reference_root, reference_xml);
                                    if (resource_list[processed_resource_index].Value == false)
                                    { // this is an ERROR, but we do not care because w;' // epic comment fail

                                    }
                                    if (!child_tag.Load_tag_file(resource_list[processed_resource_index].Key, struct_guid))
                                    { // also an error

                                    }
                                    append_to.resource_file_refs.Add(tagblock_constant_offset, child_tag.root);
                                    processed_resource_index++; // we're now unretiring this guy, because apparently the target index is either 0 or -1;
                                }
                                else if (resource_type == 1) // chunked resource type
                                {
                                    append_to.resource_file_refs.Add(tagblock_constant_offset, process_highlevel_struct(next_struct_index, struct_guid));
                                    // we cant do anything with that data so we completely ignore it
                                }
                                else // seems to be present in 'hsc_' ('hsc*') tags, although its not clear what the purpose is, maybe its an extension??
                                {
                                    //append_to.resource_file_refs.Add(tagblock_constant_offset, process_highlevel_struct(next_struct_index, struct_guid));
                                    append_to.resource_file_refs.Add(tagblock_constant_offset, null);
                                    //throw new Exception("resource is neither chunked, nor a mini tagfile??");
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
            private tag_header header;
            private tag_dependency[]? dependencies; // header.DependencyCount
            private data_block[]? data_blocks; // header.DataBlockCount
            // offset , child struct index
            private Dictionary<uint, uint>[] struct_links;
            private tag_def_structure[]? tag_structs; // header.TagStructCount
            // offset , child data_references index
            private Dictionary<uint, uint>[] data_links;
            private data_reference[]? data_references; // header.DataReferenceCount
            private tag_fixup_reference[]? tag_fixup_references; // header.TagReferenceCount

            //public string_id_reference[] string_id_references; // potentially unused? double check
            private byte[]? local_string_table; // header.StringTableSize
            // also non-nullable so we dont have to check if its null or not
            private zoneset_header zoneset_info;
            private zoneset_instance[]? zonesets; // zoneset_info.ZoneSetCount

            // and non-required stuff i guess
            private byte[]? tag_data; // like the actual values that the tag holds, eg. projectile speed and all those thingos
            private byte[]? tag_resource; // ONLY SEEN TO BE USED IN MAT FILES
            private byte[]? actual_tag_resource; // used in bitmap files

            //private byte[]? raw_file_bytes; // used for reading raw files


            //public byte[]? unmapped_header_data; // used for debugging unmapped structures in headers
            private byte[]? header_data; // tag structs require us to store the WHOLE header data so the offsets match
            // we could likely setup something so we only use the unmarked and subtract the 



            // ////////////// //
            // TAG COMPILING //
            // //////////// //
            private bool is_unk0x12_struct(XmlNode target_node){
                return (target_node.Attributes["UNK0x12"] != null);
            }
            public struct compiled_tag{
                public byte[] tag_bytes;
                public List<byte[]> resource_bytes;
            }
            public compiled_tag compile(){
                tag_compiler compiler = new(this);
                return compiler.get_compiled_data();
            }
            class tag_compiler{
                public tag_compiler(tag __tag){
                    _tag = __tag;
                }
                compiled_tag output_compiled_tag;
                public compiled_tag get_compiled_data(){
                    output_compiled_tag.resource_bytes = new(); // im starting to think we dont actually need to initialize these kinda things??
                    output_compiled_tag.tag_bytes = compile_tag(_tag.root, false);
                    // ok and then we spit out the chunk resources if we have any i guess
                    foreach (var v in _tag.resource_list){
                        // i dont think we need to validate whehter the bools are all matching or not, because we do that when we load the tag
                        // however we would have to check whats up when we're adding new resources i think
                        if (v.Value == false) // is a chunked file
                            output_compiled_tag.resource_bytes.Add(v.Key);
                    }
                    return output_compiled_tag;
                }
                tag _tag;

                tag_header output_tag_header; // we'll edit this in a moment, but we want to carry over the unknowns

                List<data_block> output_data_blocks;
                List<tag_dependency> output_tag_dependencies;

                List<tag_def_structure> output_structs;
                List<data_reference> output_data_references;
                List<tag_fixup_reference> output_tag_fixup_references;
                List<char> stringtable; // we should just null this out as we dont use it

                // we are probably not ever going to use this, so we could probably just export as they are
                zoneset_header output_zoneset_header;
                List<zoneset_instance> output_zonest_instances;

                List<byte> output_tagdata;
                List<byte> output_tag_resource;
                List<byte> output_actual_tag_resource;
                byte[] extra_unknown_headerdata;

                // we need to support compiling tag segements, meainging we need to take root struct as a variable, so we can target resources
                private byte[] compile_tag(tagdata_struct at_struct, bool resource_mode) {
                    if (_tag.Initialized == false)
                        throw new Exception("cannot compile a tag that is not initialized!!");

                    output_tag_header = _tag.header; // we'll edit this in a moment, but we want to carry over the unknowns + defaults

                    output_data_blocks = new();
                    // data_blocks
                    output_tag_dependencies = new();

                    output_structs = new();
                    output_data_references = new();
                    output_tag_fixup_references = new();
                    stringtable = new(); // we should just nullk this out as we dont use it

                    // clear useless junk
                    output_tag_header.AssetChecksum = 0;

                    // write out the alt guid from the whatever struct we're given
                    XmlNode root_node = _tag.reference_root.SelectSingleNode("_" + at_struct.GUID);
                    output_tag_header.RootStructAltGUID = Convert.ToUInt64(root_node.Attributes["Alt"].Value, 16);

                    // we are probably not ever going to use this, so we could probably just export as they are
                    // if we're compiling a resource, then we probably do not want to inherit zonset data
                    if (resource_mode){
                        // already nulled or something // output_zoneset_header;
                        output_zoneset_header.Unk_0x00 = 2; // this is required or halo will fail to load
                        output_zonest_instances = new();
                        output_tag_header.Unk_0x34 = 0; // idk what this is but resources tend to have different values
                        extra_unknown_headerdata = new byte[0];
                        output_tag_header.IsResource = 1;
                    } else{
                        output_zoneset_header = _tag.zoneset_info;
                        output_zonest_instances = _tag.zonesets.ToList();
                        extra_unknown_headerdata = _tag.unmarked_header_data;
                        output_tag_header.IsResource = 0;
                    }


                    output_tagdata = new();
                    output_tag_resource = new();
                    output_actual_tag_resource = new();

                    // create structure header for our root struct
                    tag_def_structure output_struct = new();
                    output_struct.FieldBlock = -1; // -1 because it doesn't have a parent block
                    output_struct.FieldOffset = 0; // idk if this is what we're supposed to default it to or not
                    output_struct.GUID_1 = Convert.ToInt64(at_struct.GUID.Substring(0, 16), 16);
                    output_struct.GUID_2 = Convert.ToInt64(at_struct.GUID.Substring(16), 16);
                    output_struct.TargetIndex = 0; // give it index 0, as that will be the first data block we write
                    // then check for the special unk12 indicator
                    if (_tag.is_unk0x12_struct(root_node)) output_struct.Unk_0x12 = 1;
                    else output_struct.Unk_0x12 = 0;

                    output_structs.Add(output_struct);

                    // convert all the organized data back into its compiled blocks
                    compile_tagblock(at_struct, 0);

                    // then we want to write all that data into an accurate tag file, which for now we'll just write it all to a byte array

                    // first we'll attempt to calculate the tag size
                    int tag_size = tag_header_size;
                    tag_size += tag_dependency_size * output_tag_dependencies.Count();
                    tag_size += data_block_size * output_data_blocks.Count();
                    tag_size += tag_def_structure_size * output_structs.Count();
                    tag_size += data_reference_size * output_data_references.Count();
                    tag_size += tag_fixup_reference_size * output_tag_fixup_references.Count();
                    tag_size += stringtable.Count();

                    int zoneset_size = zoneset_header_size;
                    // we have to go in and count the size of each & every silly zonset thing
                    foreach (zoneset_instance zoneset_inst in output_zonest_instances) {
                        zoneset_size += zoneset_instance_header_size;
                        // then process each item of the zoneset instance
                        for (int i = 0; i < zoneset_inst.zonset_tags.Length; i++) zoneset_size += zoneset_tag_size;
                        for (int i = 0; i < zoneset_inst.zonset_footer_tags.Length; i++) zoneset_size += zoneset_tag_size;
                        for (int i = 0; i < zoneset_inst.zonset_parents.Length; i++) zoneset_size += 4;
                    }
                    output_tag_header.ZoneSetDataSize = (uint)zoneset_size;
                    tag_size += zoneset_size;
                    tag_size += extra_unknown_headerdata.Length; // since we apparently need to include this junk
                    int total_header_size = tag_size;

                    tag_size += output_tagdata.Count();
                    tag_size += output_tag_resource.Count();
                    tag_size += output_actual_tag_resource.Count();

                    // just before we write to bytes, we need to fixup the tag header
                    // the only things that need changing are item counts & sizes
                    output_tag_header.DependencyCount = output_tag_dependencies.Count();
                    output_tag_header.DataBlockCount = output_data_blocks.Count();
                    output_tag_header.TagStructCount = output_structs.Count();
                    output_tag_header.DataReferenceCount = output_data_references.Count();
                    output_tag_header.TagReferenceCount = output_tag_fixup_references.Count();
                    output_tag_header.StringTableSize = (uint)stringtable.Count();

                    // TODO: WE NEED TO UPDATE THE SIZE OF THE ZONSETS???

                    output_tag_header.HeaderSize = (uint)total_header_size;
                    output_tag_header.DataSize = (uint)output_tagdata.Count();
                    output_tag_header.ResourceDataSize = (uint)output_tag_resource.Count();
                    output_tag_header.ActualResoureDataSize = (uint)output_actual_tag_resource.Count();

                    output_tag_header.HeaderAlignment = 0; // always 0 for some reason

                    output_tag_header.TagDataAlightment = 2;
                    output_tag_header.ResourceDataAligment = 2;
                    output_tag_header.ActualResourceDataAligment = 2;
                    // we just going to align everything by 2 for now, which i assume means min packing size of 4 bytes
                    //if (output_tagdata.Count() > 0) output_tag_header.TagDataAlightment = 2;
                    //else output_tag_header.TagDataAlightment = _tag.header.TagDataAlightment;

                    //if (output_tag_resource.Count() > 0) output_tag_header.ResourceDataAligment = 2;
                    //else output_tag_header.ResourceDataAligment = _tag.header.TagDataAlightment;

                    //if (output_actual_tag_resource.Count() > 0) output_tag_header.ActualResourceDataAligment = 2;
                    //else output_tag_header.ActualResourceDataAligment = _tag.header.TagDataAlightment;


                    // now we start writing
                    byte[] output = new byte[tag_size];
                    int current_offset = 0;
                    current_offset += CopyStructTo(output_tag_header, output, current_offset);

                    current_offset += CopyListStructTo(output_tag_dependencies, output, current_offset);
                    current_offset += CopyListStructTo(output_data_blocks, output, current_offset);
                    current_offset += CopyListStructTo(output_structs, output, current_offset);
                    current_offset += CopyListStructTo(output_data_references, output, current_offset);
                    current_offset += CopyListStructTo(output_tag_fixup_references, output, current_offset);
                    current_offset += CopyListStructTo(stringtable, output, current_offset); // empty anyway, so speed does not matter

                    // TODO: we need autogenerate these or something idk, we're just copying the data exactly as it was but with way too many extra steps

                    current_offset += CopyStructTo(output_zoneset_header, output, current_offset);
                    foreach (zoneset_instance zoneset_inst in output_zonest_instances) {
                        current_offset += CopyStructTo(zoneset_inst.header, output, current_offset);
                        current_offset += CopyArrayStructTo(zoneset_inst.zonset_tags, output, current_offset);
                        current_offset += CopyArrayStructTo(zoneset_inst.zonset_footer_tags, output, current_offset);
                        current_offset += CopyArrayStructTo(zoneset_inst.zonset_parents, output, current_offset);
                    }
                    // and then our epic unmarked data (FU bungi)
                    if (extra_unknown_headerdata.Length > 0){
                        Array.Copy(extra_unknown_headerdata, 0, output, current_offset, extra_unknown_headerdata.Length);
                        current_offset += extra_unknown_headerdata.Count();
                    }

                    // do a basic block copy on these as its probably quicker
                    if (output_tagdata.Count() > 0){
                        byte[] src = output_tagdata.ToArray();
                        Array.Copy(src, 0, output, current_offset, src.Length);
                        current_offset += output_tagdata.Count();
                    }
                    if (output_tag_resource.Count() > 0){
                        byte[] src = output_tag_resource.ToArray();
                        Array.Copy(src, 0, output, current_offset, src.Length);
                        current_offset += output_tag_resource.Count();
                    }
                    if (output_actual_tag_resource.Count() > 0){
                        byte[] src = output_actual_tag_resource.ToArray();
                        Array.Copy(src, 0, output, current_offset, src.Length);
                        current_offset += output_actual_tag_resource.Count(); // potentially unneeded
                    }
                    return output;
                }

                // to get the target index that we're about to allocate, we just count how many data blocks have been allocated, which is also what we do here to get the new index
                void compile_tagblock(tagdata_struct _struct, int struct_def_index){

                    XmlNode currentStruct = _tag.reference_root.SelectSingleNode("_" + _struct.GUID);
                    int tagblock_item_size = Convert.ToInt32(currentStruct.Attributes["Size"].Value, 16);

                    data_block struct_data_block = new();
                    struct_data_block.Section = 1; // tagdata

                    // we also need to check whether the size matches with tagdata alignment
                    //if ((struct_data_block.Size & 0b11) != 0)
                    //    throw new Exception("tagdata does not meet 0b100 alignemnt!!");
                    // caculate padding
                    int padding = output_tagdata.Count() % 4;
                    for (int padi = 0; padi < padding; padi++) output_tagdata.Add(0);
                    struct_data_block.Padding = (ushort)padding;

                    // then we can assign the offset & size and whatever else
                    struct_data_block.Offset = (ulong)output_tagdata.Count(); // gets the current allocated data size
                    int field_block = output_data_blocks.Count(); // gets the next available datablock index


                    // we need to iterate through all the blocks, add up their tagdata & other reference things
                    int current_offset = 0;
                    // we have to precompute the size of the data block, so the child blocks do not steal our index
                    // note we now dont actually need to do that, as we preprocess the tagdata, before compiling the child blocks
                    struct_data_block.Size = (uint)(_struct.blocks.Count * tagblock_item_size);
                    output_data_blocks.Add(struct_data_block);
                    // separate block so we can append all the tagdata first, lest we want our data to get spliced with other data
                    foreach (thing block in _struct.blocks){
                        if (tagblock_item_size != block.tag_data.Length)
                            throw new Exception("tagblock contained data that did not align with expected size!!");
                        output_tagdata.AddRange(block.tag_data);
                        current_offset += block.tag_data.Length;
                    }
                    current_offset = 0;
                    foreach (thing block in _struct.blocks){
                        compile_struct(currentStruct, block, struct_def_index, field_block, current_offset, 0);
                        current_offset += block.tag_data.Length;
                    }

                    return;
                }

                void compile_struct(XmlNode currentStruct, thing _struct, int struct_def_index, int field_block, int block_offset, int relative_offset){

                    for (int i = 0; i < currentStruct.ChildNodes.Count; i++){
                        XmlNode node = currentStruct.ChildNodes[i];


                        int offset = Convert.ToInt32(node.Attributes?["Offset"]?.Value, 16);
                        int tagblock_offset = relative_offset + offset;
                        int field_offset = block_offset + offset;

                        int type = Convert.ToInt32(node.Name.Substring(1), 16);
                        switch (type){
                            // these two are just for iterating through the structs, we dont actually need to do anything special to compile them
                            case 0x38:{ // _field_struct 
                                    string next_guid = node.Attributes?["GUID"]?.Value;
                                    XmlNode next_node = _tag.reference_root.SelectSingleNode("_" + next_guid);
                                    // test whether this struct is marked as unk0x12
                                    if (_tag.is_unk0x12_struct(next_node)){
                                        tag_def_structure output_struct = new();
                                        output_struct.Type = 4;
                                        output_struct.FieldBlock = field_block;
                                        output_struct.FieldOffset = (uint)field_offset;
                                        output_struct.Unk_0x12 = 1;
                                        output_struct.TargetIndex = -1;
                                        // update guid if it has an override
                                        if (next_node.Attributes["Override"] != null)
                                            next_guid = next_node.Attributes["Override"].Value;
                                        // convert guid
                                        output_struct.GUID_1 = Convert.ToInt64(next_guid.Substring(0, 16), 16);
                                        output_struct.GUID_2 = Convert.ToInt64(next_guid.Substring(16), 16);

                                        output_structs.Add(output_struct);
                                    }


                                    compile_struct(next_node, _struct, struct_def_index, field_block, field_offset, tagblock_offset);
                                }break;
                            case 0x39:{ // _field_array
                                    int array_length = Convert.ToInt32(node.Attributes?["Count"]?.Value);

                                    string next_guid = node.Attributes?["GUID"]?.Value;
                                    // we could just copy paste the above struct but i'd rather not
                                    XmlNode next_node = _tag.reference_root.SelectSingleNode("_" + next_guid);
                                    if (_tag.is_unk0x12_struct(next_node)) throw new Exception("array marked with Unk0x12??? need to investigate this");
                                    
                                    // make sure this is called on the child node not the current node 
                                    int array_struct_size = Convert.ToInt32(next_node.Attributes?["Size"]?.Value, 16);

                                    for (int arr_index = 0; arr_index < array_length; arr_index++){
                                        int array_offset = arr_index * array_struct_size;
                                        compile_struct(next_node, _struct, struct_def_index, field_block, field_offset + array_offset, tagblock_offset + array_offset);
                                }}break;

                            // these 4 all need us to write special stuff into our tag

                            case 0x40:{ // _field_block_v2
                                  // NOTE: local offset, not offset across entire datablock, as we localize all offsets to make things easier to work with
                                  // this also applies for all following struct types
                                    tag_def_structure output_struct = new();
                                    output_struct.Type = 1;
                                    output_struct.FieldBlock = field_block;
                                    output_struct.FieldOffset = (uint)field_offset;
                                    // convert guid
                                    string next_guid = node.Attributes?["GUID"]?.Value;
                                    XmlNode next_node = _tag.reference_root.SelectSingleNode("_" + next_guid);

                                    // update guid if it has an override
                                    if (next_node.Attributes["Override"] != null)
                                        next_guid = next_node.Attributes["Override"].Value;

                                    output_struct.GUID_1 = Convert.ToInt64(next_guid.Substring(0, 16), 16);
                                    output_struct.GUID_2 = Convert.ToInt64(next_guid.Substring(16), 16);
                                    if (_tag.is_unk0x12_struct(next_node)) output_struct.Unk_0x12 = 1;
                                    else output_struct.Unk_0x12 = 0;
                                    // fill in the target index for the struct
                                    if (_struct.tag_block_refs.TryGetValue((ulong)tagblock_offset, out var thinger) && thinger.blocks.Count() > 0){
                                        output_struct.TargetIndex = output_data_blocks.Count();
                                        int this_struct_def_index = output_structs.Count();
                                        output_structs.Add(output_struct);
                                        compile_tagblock(thinger, this_struct_def_index);
                                    }else{ // else fill blank for this guy
                                        output_struct.TargetIndex = -1;
                                        output_structs.Add(output_struct);
                                }}break;

                            case 0x41:{ // _field_reference_v2
                                  // ok screw it, we're just going to read the group and whatever straight from the tagdata at this offset
                                    uint tagID = BitConverter.ToUInt32(_struct.tag_data[(tagblock_offset + 0x8)..(tagblock_offset + 0xC)]);
                                    ulong assetID = BitConverter.ToUInt64(_struct.tag_data[(tagblock_offset + 0xC)..(tagblock_offset + 0x14)]);
                                    int group = BitConverter.ToInt32(_struct.tag_data[(tagblock_offset + 0x14)..(tagblock_offset + 0x18)]);
                                    // first we need to setup a tagreference thing here
                                    // then we need to append this to the list of tag dependencies if it isn't already added

                                    // we have to check for the flag that determines whether the tagref is not allowed to be appended to dependencies?
                                    int tagref_flags = Convert.ToInt32(node.Attributes?["Flags"]?.Value);

                                    int tagref_dependency_index = -1;
                                    if (tagID != 0xFFFFFFFF && group != -1 && ((tagref_flags & 0x10) == 0)){
                                        // see if we already have it listed
                                        for (int dep_index = 0; dep_index < output_tag_dependencies.Count; dep_index++)
                                            if (output_tag_dependencies[dep_index].GlobalID == tagID){
                                                tagref_dependency_index = dep_index;
                                                break;
                                            }
                                        // otherwise we'll add a new entry for the dependency 
                                        if (tagref_dependency_index == -1){
                                            tag_dependency tag_dep = new();
                                            tag_dep.GlobalID = tagID;
                                            tag_dep.AssetID = assetID;
                                            tag_dep.GroupTag = group;
                                            // these are both unused i believe
                                            tag_dep.NameOffset = 0;
                                            tag_dep.Unk_0x14 = -1; // what is this even for, it must be an index if most have -1

                                            tagref_dependency_index = output_tag_dependencies.Count;
                                            output_tag_dependencies.Add(tag_dep);
                                    }}
                                    tag_fixup_reference tag_ref = new();
                                    tag_ref.DepdencyIndex = tagref_dependency_index;
                                    tag_ref.FieldBlock = field_block;
                                    tag_ref.FieldOffset = (uint)field_offset;
                                    // unused
                                    tag_ref.NameOffset = 0;

                                    // then add to list
                                    output_tag_fixup_references.Add(tag_ref);
                                }break;

                            case 0x42:{ // _field_data_v2
                                    data_reference output_struct = new();

                                    output_struct.FieldBlock = field_block;
                                    output_struct.FieldOffset = (uint)field_offset;
                                    output_struct.ParentStructIndex = struct_def_index; // we had to pass this along just so this guy could have it, seems a little useless though
                                    // probably unused? im not sure what this is for
                                    output_struct.Unk_0x04 = 0;
                                    // fill in the target index for the struct
                                    if (_struct.tag_resource_refs.TryGetValue((ulong)tagblock_offset, out var data_resource) && data_resource.Length > 0){
                                        // gets the next available datablock index
                                        output_struct.TargetIndex = output_data_blocks.Count();

                                        // then generate a new data block for this guy
                                        data_block struct_data_block = new();
                                        struct_data_block.Size = (uint)data_resource.Length;

                                        // accoding to the alignemnt system, we need to make sure the data is aligned correctly
                                        // so we have to add padding if the offset is not quite even
                                        
                                        // determine which section this belongs to
                                        int probable_section = Convert.ToInt32(node.Attributes?["Int2"]?.Value);
                                        if (probable_section == 0){ // tagdata section
                                            // caculate padding
                                            int padding = output_tagdata.Count() % 4;
                                            for (int padi = 0; padi < padding; padi++) output_tagdata.Add(0);
                                            struct_data_block.Padding = (ushort)padding;
                                            // write data
                                            struct_data_block.Section = 1; 
                                            struct_data_block.Offset = (ulong)output_tagdata.Count(); 
                                            output_tagdata.AddRange(data_resource);
                                        } else if (probable_section == 2){ // resource data section
                                            // caculate padding
                                            int padding = output_tag_resource.Count() % 4;
                                            for (int padi = 0; padi < padding; padi++) output_tag_resource.Add(0);
                                            struct_data_block.Padding = (ushort)padding;
                                            // write data
                                            struct_data_block.Section = 2;
                                            struct_data_block.Offset = (ulong)output_tag_resource.Count(); 
                                            output_tag_resource.AddRange(data_resource);
                                        } else{ // if (probable_section == 4){ // actual resource data section
                                            // caculate padding
                                            int padding = output_actual_tag_resource.Count() % 4;
                                            for (int padi = 0; padi < padding; padi++) output_actual_tag_resource.Add(0);
                                            struct_data_block.Padding = (ushort)padding;
                                            // write data
                                            struct_data_block.Section = 3;
                                            struct_data_block.Offset = (ulong)output_actual_tag_resource.Count();
                                            output_actual_tag_resource.AddRange(data_resource);
                                        }
                                        //else throw new Exception("unkown int2 section index??");

                                        // then add our new data block to the list
                                        output_data_blocks.Add(struct_data_block);
                                    }
                                    // else fill blank for this guy
                                    else output_struct.TargetIndex = -1;
                                    output_data_references.Add(output_struct);

                                }break;

                            case 0x43:{ // tag_resource
                                    tag_def_structure output_struct = new();
                                    output_struct.FieldBlock = field_block;
                                    output_struct.FieldOffset = (uint)field_offset;
                                    // convert guid
                                    string next_guid = node.Attributes?["GUID"]?.Value;

                                    // check for overrides, which hopefully should never exist on resources?
                                    XmlNode next_node = _tag.reference_root.SelectSingleNode("_" + next_guid);
                                    if (next_node.Attributes["Override"] != null)
                                        next_guid = next_node.Attributes["Override"].Value;

                                    output_struct.GUID_1 = Convert.ToInt64(next_guid.Substring(0, 16), 16);
                                    output_struct.GUID_2 = Convert.ToInt64(next_guid.Substring(16), 16);

                                    int is_chunked = BitConverter.ToInt32(_struct.tag_data[(tagblock_offset + 12)..(tagblock_offset + 16)]);
                                    if (is_chunked == 0){
                                        output_struct.Type = 2; // for standalone tag resource
                                        output_struct.Unk_0x12 = 0;
                                        // then we have to process this as a mini/standalone tag?
                                        // load subtag as bytes, & set target index if valid
                                        if (_struct.resource_file_refs.TryGetValue((ulong)tagblock_offset, out var thinger) && thinger != null && thinger.blocks.Count() > 0){
                                            tag_compiler resource_compiler = new(_tag);
                                            byte[] resource_bytes = resource_compiler.compile_tag(thinger, true);
                                            output_struct.TargetIndex = 0; // apparently we dont index the resource or anything
                                            output_compiled_tag.resource_bytes.Add(resource_bytes);
                                        // else we do not assign a valid resource for this guy
                                        }else output_struct.TargetIndex = -1;
                                        output_structs.Add(output_struct); // both dont interfere with the order so we can run this after
                                    } else{
                                        output_struct.Type = 3; // for chunked resource
                                        output_struct.Unk_0x12 = 1;
                                        // otherwise this is just a chunk file resource, and we'll append the resource in the main compile function
                                        // and the struct itself is contained within the main tag file, so just pretend this is a tagblock basically
                                        // fill in the target index for the struct
                                        if (_struct.resource_file_refs.TryGetValue((ulong)tagblock_offset, out var thinger) && thinger.blocks.Count() > 0){
                                            output_struct.TargetIndex = output_data_blocks.Count();
                                            int this_struct_def_index = output_structs.Count();
                                            output_structs.Add(output_struct); // to match the order, we have to do this before compiling the tagblock
                                            compile_tagblock(thinger, this_struct_def_index);
                                        // else somehow this resource is empty, we should probably throw an exception
                                        }else throw new Exception("chunked resources should NOT be empty!!"); // output_struct.TargetIndex = -1;
                                    }
                                }break;
                        }
                    }
                }

                
            }



            // //////////////// //
            // TAG EDITING API //
            // ////////////// //
            public void set_tagID(uint new_tagid){
                byte[] tagid_bytes = BitConverter.GetBytes(new_tagid);
                // the tagid is stored at 0x8 of the main tagstruct data
                Array.Copy(tagid_bytes, 0, root.blocks[0].tag_data, 8, 4);
            }
            /* PATH EXAMPLES 
               "super node parent mappings[0].parent_super_node_index" // param nested in array type (tagblock)
               "build identifier.structure importer version" // param nested in struct
            */
            public tagdata_struct get_tagblock(string target, thing? target_block = null, string block_guid = null){
                thing current_block = apply_root_if_null(target_block, ref block_guid);
                // we need to find the xml node so we can get the offset, then we can use that to get from the dictionary
                ulong offset = 0;
                recurse_block_search(ref current_block, block_guid, target, ref offset);
                // supports both tagblocks & resource blocks because why not
                if (current_block.tag_block_refs.TryGetValue(offset, out tagdata_struct? next_tagblock))
                    return next_tagblock;
                else if (current_block.resource_file_refs.TryGetValue(offset, out tagdata_struct? next_resource))
                    return next_resource;
                else throw new Exception("tagblock path '" + target + "' does not exist");
            }
            
            public void set_datablock(string path, byte[] new_bytes, thing? target_block = null, string block_guid = null){
                thing current_block = apply_root_if_null(target_block, ref block_guid);
                ulong block_offset = 0;
                // error handling should be controlled outside of this?
                XmlNode result = recurse_block_search(ref current_block, block_guid, path, ref block_offset);
                if (result.Name != "_42") throw new Exception("parameter path does not return a datablock parameter");
                current_block.tag_resource_refs[block_offset] = new_bytes;
            }
            public byte[] get_datablock(string path, thing? target_block = null, string block_guid = null){
                thing current_block = apply_root_if_null(target_block, ref block_guid);
                ulong block_offset = 0;
                // error handling should be controlled outside of this?
                XmlNode result = recurse_block_search(ref current_block, block_guid, path, ref block_offset);
                if (result.Name != "_42") throw new Exception("parameter path does not return a datablock parameter");
                return current_block.tag_resource_refs[block_offset];
            }

            
            public void set_number(string path, string number, thing? target_block = null, string block_guid = null){
                thing current_block = apply_root_if_null(target_block, ref block_guid);

                ulong block_offset = 0;
                // error handling should be controlled outside of this?
                XmlNode result = recurse_block_search(ref current_block, block_guid, path, ref block_offset);
                int group_index = Convert.ToInt32(result.Name.Substring(1), 16);
                //determine what kind of number this is, and whether its compatible with our input
                byte[] output;
                switch (group_index){
                    case 4: // signed byte
                        output = new byte[1] { (byte)Convert.ToChar(number) };
                        break;
                    case 5: // signed short
                        output = BitConverter.GetBytes(Convert.ToInt16(number));
                        break;
                    case 6: // signed short
                        output = BitConverter.GetBytes(Convert.ToInt32(number));
                        break;
                    case 7: // signed short
                        output = BitConverter.GetBytes(Convert.ToInt64(number));
                        break;
                    case 0x3C: // unsigned byte
                        output = new byte[1] { Convert.ToByte(number) };
                        break;
                    case 0x3D: // unsigned short
                        output = BitConverter.GetBytes(Convert.ToUInt16(number));
                        break;
                    case 0x3E: // unsigned short
                        output = BitConverter.GetBytes(Convert.ToUInt32(number));
                        break;
                    case 0x3F: // unsigned short
                        output = BitConverter.GetBytes(Convert.ToUInt64(number));
                        break;
                    case 0x14: // float
                        output = BitConverter.GetBytes(Convert.ToSingle(number));
                        break;
                    case 0x15: // fraction float
                        float single = Convert.ToSingle(number);
                        if (single < 0.0f || single > 1.0f) throw new Exception("cannot assign value to fraction (must be between 0.0 & 1.0)");
                        output = BitConverter.GetBytes(single);
                        break;
                    default: throw new Exception("target parameter is not a number!");
                }
                Array.Copy(output, 0, current_block.tag_data, (int)block_offset, output.Length);
            }
            public string get_number(string path, thing? target_block = null, string block_guid = null){
                thing current_block = apply_root_if_null(target_block, ref block_guid);
                ulong block_offset = 0;
                // error handling should be controlled outside of this?
                XmlNode result = recurse_block_search(ref current_block, block_guid, path, ref block_offset);
                int group_index = Convert.ToInt32(result.Name.Substring(1), 16);
                //determine what kind of number we're supposed to output
                int param_length = param_group_sizes[group_index];
                byte[] data = current_block.tag_data[(int)block_offset..((int)block_offset + param_length)];
                switch (group_index){
                    case 4: // signed byte
                        return current_block.tag_data[block_offset].ToString();
                    case 5: // signed short
                        return BitConverter.ToInt16(data).ToString();
                    case 6: // signed int
                        return BitConverter.ToInt32(data).ToString();
                    case 7: // signed long
                        return BitConverter.ToInt64(data).ToString();
                    case 0x3C: // unsigned byte
                        return ((char)current_block.tag_data[block_offset]).ToString();
                    case 0x3D: // unsigned short
                        return BitConverter.ToUInt16(data).ToString();
                    case 0x3E: // unsigned int
                        return BitConverter.ToUInt32(data).ToString();
                    case 0x3F: // unsigned long
                        return BitConverter.ToUInt64(data).ToString();
                    case 0x14: // float
                    case 0x15: // fraction float
                        return BitConverter.ToSingle(data).ToString();
                    default: throw new Exception("target parameter is not a number!");
                }
            }
            public Vector3 get_float3(string path, thing? target_block = null, string block_guid = null){
                thing current_block = apply_root_if_null(target_block, ref block_guid);
                ulong block_offset = 0;
                // error handling should be controlled outside of this?
                XmlNode result = recurse_block_search(ref current_block, block_guid, path, ref block_offset);
                int group_index = Convert.ToInt32(result.Name.Substring(1), 16);
                int param_length = param_group_sizes[group_index];
                if (param_length != 12) throw new Exception("param did not have the expected size! we should actually be checking the type not the length!!!!");
                float f1 = BitConverter.ToSingle(current_block.tag_data[(int)block_offset..((int)block_offset + 4)]);
                float f2 = BitConverter.ToSingle(current_block.tag_data[(int)(block_offset+4)..((int)block_offset + 8)]);
                float f3 = BitConverter.ToSingle(current_block.tag_data[(int)(block_offset+8)..((int)block_offset + 12)]);
                return new Vector3(f1, f2, f3);
            }

            public void set_basic(string path, byte[] new_bytes, thing? target_block = null, string block_guid = null){
                thing current_block = apply_root_if_null(target_block, ref block_guid);
                ulong block_offset = 0;
                // error handling should be controlled outside of this?
                XmlNode result = recurse_block_search(ref current_block, block_guid, path, ref block_offset);
                int group_index = Convert.ToInt32(result.Name.Substring(1), 16);
                int param_length = param_group_sizes[group_index];
                if (param_length != new_bytes.Length) throw new Exception("mismatching input byte array length");

                Array.Copy(new_bytes, 0, current_block.tag_data, (int)block_offset, new_bytes.Length);
            }
            public byte[] get_basic(string path, thing? target_block = null, string block_guid = null){
                thing current_block = apply_root_if_null(target_block, ref block_guid);
                ulong block_offset = 0;
                // error handling should be controlled outside of this?
                XmlNode result = recurse_block_search(ref current_block, block_guid, path, ref block_offset);
                int group_index = Convert.ToInt32(result.Name.Substring(1), 16);
                int param_length = param_group_sizes[group_index];
                return current_block.tag_data[(int)block_offset..((int)block_offset+param_length)];
            }

            private thing apply_root_if_null(thing? test, ref string block_guid){
                if (test != null) return (thing)test;
                block_guid = root.GUID;
                return root.blocks[0];
            }
            // take in an arbitrary parameter path and process it, and then return the details so other functions can handle it however they want
            // especially with the ability to return a different block, as we still need to beable to read the bytes
            private XmlNode recurse_block_search(ref thing current_block, string current_guid, string target, ref ulong resulting_offset){
                XmlNode current_struct = reference_root.SelectSingleNode("_"+current_guid);
                // if the target contains a '.', then its contained inside of a struct, look for that struct
                int char_pos = target.IndexOf('.', StringComparison.Ordinal);
                if (char_pos > 0) {
                    string next_struct = target.Substring(0, char_pos);
                    string next_target = target.Substring(char_pos + 1);
                    // if it ends with square brackets then its inside of an array
                    int brack_pos = target.IndexOf('[', StringComparison.Ordinal);
                    if (brack_pos > 0 && next_struct.Last() == ']') {
                        string array_name = next_struct.Substring(0, brack_pos);
                        uint array_index = Convert.ToUInt32(next_struct.Substring(brack_pos+1, (next_struct.Length) - (brack_pos+2)));
                        // this could be either an array or a tagblock
                        XmlNode? next = current_struct.SelectSingleNode("//*[@Name = '" + array_name + "']");
                        if (next == null) throw new Exception("path array '" + array_name + "' did not result any matches");
                        uint offset = Convert.ToUInt32(next.Attributes["Offset"].Value, 16);
                        resulting_offset += offset;
                        string next_guid = next.Attributes["GUID"].Value;

                        if (next.Name == "_39") { // array
                            uint struct_max = Convert.ToUInt32(next.Attributes["Count"].Value);
                            XmlNode array_struct = reference_root.SelectSingleNode("_"+ next_guid);
                            uint struct_size = Convert.ToUInt32(array_struct.Attributes["Size"].Value, 16);
                            // check to make sure our target index is not out of bounds
                            if (array_index >= struct_max) throw new Exception("path array '" + array_name + "' index '" + array_index + "' was out of bounds");

                            resulting_offset += array_index * struct_size;
                            return recurse_block_search(ref current_block, next_guid, next_target, ref resulting_offset);
                        } else if (next.Name == "_40") { // tagblock
                            // we have to get the next block from the tagthing
                            if (current_block.tag_block_refs.TryGetValue(resulting_offset, out tagdata_struct? next_tagblock) && next_tagblock.blocks.Count > 0){
                                if (array_index >= next_tagblock.blocks.Count) throw new Exception("path tagblock array '" + array_name + "' index '" + array_index + "' was out of bounds");
                                current_block = next_tagblock.blocks[(int)array_index];
                                resulting_offset = 0;
                                return recurse_block_search(ref current_block, next_tagblock.GUID, next_target, ref resulting_offset);
                            } else throw new Exception("path tagblock struct '" + next_struct + "' was a valid path but the resource does not exist");
                        } else throw new Exception("path array '" + array_name + "' was not a valid array type");

                    } else { // process as struct
                        XmlNode? next = current_struct.SelectSingleNode("//*[@Name = '" + next_struct + "']");
                        // it could be either a struct type or resource type
                        if (next == null) throw new Exception("path struct '" + next_struct + "' did not result any matches");
                        uint offset = Convert.ToUInt32(next.Attributes["Offset"].Value, 16);
                        resulting_offset += offset;
                        string next_guid = next.Attributes["GUID"].Value;

                        if (next.Name == "_38") { // struct
                            return recurse_block_search(ref current_block, next_guid, next_target, ref resulting_offset);
                        } else if (next.Name == "_43") { // resource
                            // we have to get the next block from the tagthing
                            if (current_block.resource_file_refs.TryGetValue(resulting_offset, out tagdata_struct? next_resource) && next_resource.blocks.Count > 0){
                                current_block = next_resource.blocks[0];
                                resulting_offset = 0;
                                return recurse_block_search(ref current_block, next_resource.GUID, next_target, ref resulting_offset);
                            } else throw new Exception("path resource struct '" + next_struct + "' was a valid path but the resource does not exist");
                        } else throw new Exception("path struct '" + next_struct + "' was not a valid struct type");
                }}
                // else we're in the place that we want to be, just return whatever shows up
                XmlNode? target_node = current_struct.SelectSingleNode("//*[@Name = '" + target + "']");
                if (target_node == null) throw new Exception("param '" + target + "' did not result any matches");
                // update offset so we dont have to outside of this function
                resulting_offset += Convert.ToUInt32(target_node.Attributes["Offset"].Value, 16);
                return target_node;
            }

            private static int[] param_group_sizes = new int[]{
                32,  //  _0 // _field_string
                256, //  _1 // _field_long_string
                4,   //  _2 // _field_string_id
                4,   //  _3 // 
                1,   //  _4 // _field_char_integer
                2,   //  _5 // _field_short_integer
                4,   //  _6 // _field_long_integer
                8,   //  _7 // _field_int64_integer
                4,   //  _8 // _field_angle
                4,   //  _9 // _field_tag
                1,   //  _A // _field_char_enum
                2,   //  _B // _field_short_enum
                4,   //  _C // _field_long_enum
                4,   //  _D // _field_long_flags
                2,   //  _E // _field_word_flags
                1,   //  _F // _field_byte_flags
                4,   // _10 // _field_point_2d
                4,   // _11 // _field_rectangle_2d
                4,   // _12 // _field_rgb_color
                4,   // _13 // _field_argb_color 
                4,   // _14 // _field_real
                4,   // _15 // _field_real_fraction
                8,   // _16 // _field_real_point_2d
                12,  // _17 // _field_real_point_3d
                8,   // _18 // _field_real_vector_2d
                12,  // _19 // _field_real_vector_3d
                16,  // _1A // _field_real_quaternion
                8,   // _1B // _field_real_euler_angles_2d
                12,  // _1C // _field_real_euler_angles_3d
                12,  // _1D // _field_real_plane_2d
                16,  // _1E // _field_real_plane_3d
                12,  // _1F // _field_real_rgb_color
                16,  // _20 // _field_real_argb_color
                4,   // _21 // _field_real_hsv_colo
                4,   // _22 // _field_real_ahsv_color
                4,   // _23 // _field_short_bounds
                8,   // _24 // _field_angle_bounds
                8,   // _25 // _field_real_bounds
                8,   // _26 // _field_real_fraction_bounds
                4,   // _27 // 
                4,   // _28 //
                4,   // _29 // _field_long_block_flags
                4,   // _2A // _field_word_block_flags
                4,   // _2B // _field_byte_block_flags
                1,   // _2C // _field_char_block_index
                1,   // _2D // _field_custom_char_block_index
                2,   // _2E // _field_short_block_index
                2,   // _2F // _field_custom_short_block_index
                4,   // _30 // _field_long_block_index
                4,   // _31 // _field_custom_long_block_index
                4,   // _32 // 
                4,   // _33 // 
                0,   // _34 // _field_pad 
                0,   // _35 // _field_skip
                0,   // _36 // _field_explanation
                0,   // _37 // _field_custom
                0,   // _38 // _field_struct 
                0,   // _39 // _field_array
                4,   // _3A // 
                0,   // _3B // end of struct
                1,   // _3C // _field_byte_integer
                2,   // _3D // _field_word_integer
                4,   // _3E // _field_dword_integer
                8,   // _3F // _field_qword_integer
                20,  // _40 // _field_block_v2
                28,  // _41 // _field_reference_v2
                24,  // _42 // _field_data_v2
                16,  // _43 // tag_resource
                4,   // _44 // UNKNOWN
                4    // _45 // UNKNOWN
            };
        }

        public const int tag_header_size = 0x50;
        [StructLayout(LayoutKind.Explicit, Size = tag_header_size)] public struct tag_header
        {
            [FieldOffset(0x00)] public int     Magic; 
            [FieldOffset(0x04)] public uint    Version; 
            [FieldOffset(0x08)] public ulong   RootStructAltGUID; // this is actually a secondary GUID to identify the root struct // causes crashes if not correct
            [FieldOffset(0x10)] public ulong   AssetChecksum;  
            // these cant be uints because of how the code is setup, should probably assert if -1
            [FieldOffset(0x18)] public int     DependencyCount;
            [FieldOffset(0x1C)] public int     DataBlockCount;  
            [FieldOffset(0x20)] public int     TagStructCount; 
            [FieldOffset(0x24)] public int     DataReferenceCount; 
            [FieldOffset(0x28)] public int     TagReferenceCount; 
                                
            [FieldOffset(0x2C)] public uint    StringTableSize;
                                
            [FieldOffset(0x30)] public uint    ZoneSetDataSize;    // this is the literal size in bytes
            [FieldOffset(0x34)] public uint    Unk_0x34; // could be the block count that this file is broken up into? // important value, game will crash if incorrect
                                
            [FieldOffset(0x38)] public uint    HeaderSize; 
            [FieldOffset(0x3C)] public uint    DataSize; 
            [FieldOffset(0x40)] public uint    ResourceDataSize; 
            [FieldOffset(0x44)] public uint    ActualResoureDataSize;  // also new with infinite
                                
            [FieldOffset(0x48)] public byte    HeaderAlignment; 
            [FieldOffset(0x49)] public byte    TagDataAlightment; 
            [FieldOffset(0x4A)] public byte    ResourceDataAligment; 
            [FieldOffset(0x4B)] public byte    ActualResourceDataAligment; 

            [FieldOffset(0x4C)] public int     IsResource; // not quite sure on this one yet, need to check some more tags
        }


        public const int tag_dependency_size = 0x18;
        [StructLayout(LayoutKind.Explicit, Size = tag_dependency_size)] public struct tag_dependency // this struct looks just like a regular tag reference
        {
            [FieldOffset(0x00)] public int     GroupTag;
            [FieldOffset(0x04)] public uint    NameOffset; // NOT CORRECT (doesn't need to be)
                                
            [FieldOffset(0x08)] public ulong   AssetID;
            [FieldOffset(0x10)] public uint    GlobalID;
            [FieldOffset(0x14)] public int     Unk_0x14;  // (it seems to always be -1) // possibly padding?
        }

        public const int data_block_size = 0x10;
        [StructLayout(LayoutKind.Explicit, Size = data_block_size)] public struct data_block
        {
            [FieldOffset(0x00)] public uint    Size;
            [FieldOffset(0x04)] public ushort  Padding;   // how many unused bytes come before the offset, probably for if you were to read the file without jumping offsets or something
                                
            [FieldOffset(0x06)] public ushort  Section;    // "0 = Header, 1 = Tag Data, 2 = Resource Data" 3 would be that actual resource thingo
            [FieldOffset(0x08)] public ulong   Offset;     // "The offset of the start of the data block, relative to the start of its section."
        }

        public const int tag_def_structure_size = 0x20;
        [StructLayout(LayoutKind.Explicit, Size = tag_def_structure_size)] public struct tag_def_structure
        {
            //byte[16] GUID; // 0x00 // ok lets not attempt to use a byte array then
            [FieldOffset(0x00)] public long   GUID_1;
            [FieldOffset(0x08)] public long   GUID_2;
            // ok, so 0 is the main struct, 1 is a tagblock struct, 2 is a non-chunked resource, 3 is chunked resource, 4 is for literal structs (useless?)
            [FieldOffset(0x10)] public ushort  Type;           // "0 = Main Struct, 1 = Tag Block, 2 = Resource, 3 = Custom" 4 seems to be for notable structs (like render geo) idk if we need to include it though
            [FieldOffset(0x12)] public ushort  Unk_0x12;       // likely padding // as it turns out this is a very important value? setting it incorrectly WILL cause crashes while loading the compile module ingame
                                
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
