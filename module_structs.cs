
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


// TODO:
// 1. separate loading into its own class, so that all data used is easily disposable
// 2. loading resource data, requires more testing to understand
// 3. tag saving, this will wait until season 3 as there was a few changes that simplify the process, and/or make it more complicated


namespace Infinite_module_test
{
    public static class tag_structs
    {
        // VERSION 27 // VERSION 27 // VERSION 27 //
        // an interesting thing to note is that version 27 was the version of halo 5 forge
        // it seems someone on the engine team failed to update this number lol, because this struct has certainly changed since
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
            // load tag outputs
            public bool Initialized = false;
            public tagdata_struct? root = null;
            XmlDocument reference_xml;
            public XmlNode reference_root;
            //
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
            FileStream? tag_reader; // to be cleaned up after loading
            public bool Load_tag_file(string tag_path, string plugin_path) {
                if (!File.Exists(tag_path)) return false; // failed via invalid directory
                tag_reader = new FileStream(tag_path, FileMode.Open, FileAccess.Read);
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
                uint[] block_to_struct_links = new uint[data_blocks.Length];
                for (uint i = 0; i < tag_structs.Length; i ++){
                    if (tag_structs[i].TargetIndex == -1) continue;
                    // if (block_to_struct_links[tag_structs[i].TargetIndex] != 0) // not found to occur, however untested on more than a single tag
                    block_to_struct_links[tag_structs[i].TargetIndex] = i;
                }
                // assign the child structs links, based on their parent field block
                for (uint i = 0; i < tag_structs.Length; i++){
                    if (tag_structs[i].Type == 0) continue; // the root struct has no parent
                    struct_links[block_to_struct_links[tag_structs[i].FieldBlock]].Add(tag_structs[i].FieldOffset, i);
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
                for (uint i = 0; i < tag_structs.Length; i++) {
                    if (tag_structs[i].Type == 0) {
                        // because of how this system works, we need to find the GUID first, to figure out what plugin file to use
                        // realistically, we could rename the plugins as their guid value, but that wouldn't be very cool for the people just looking through the files (aka not user friendly)
                        string root_GUID = tag_structs[i].GUID_1.ToString("X16") + tag_structs[i].GUID_2.ToString("X16");
                        string plugin_guide_path = plugin_path + "\\GUIDs.txt";
                        string tagus_groupus = "";
                        using (StreamReader reader = File.OpenText(plugin_guide_path)) {
                            while (!reader.EndOfStream) {
                                string[] line = reader.ReadLine().Split(":");
                                if (line[1] == root_GUID) {
                                    tagus_groupus = line[0];
                                    break;
                                } } }


                        // setup reference plugin file
                        reference_xml = new XmlDocument();
                        reference_xml.Load(plugin_path + "\\" + tagus_groupus + ".xml");
                        reference_root = reference_xml.SelectSingleNode("root");


                        root = process_highlevel_struct(i, root_GUID);
                        break;
                }}




                Initialized = true;
                return true;
            }
            public class tagdata_struct
            {
                public List<thing> blocks = new();
            }
            public struct thing
            {
                // GUID needed, as we'll use that to figure out which struct to reference when interpretting the byte array
                public string GUID;
                // array of the struct bytes
                public byte[] tag_data;
                // offset, tagblock instance
                public Dictionary<ulong, tagdata_struct> tag_block_refs;
                // offset, resource data
                public Dictionary<ulong, byte[]> tag_resource_refs;
            }
            private tagdata_struct? process_highlevel_struct(uint struct_index, string GUID, int block_count = 1)
            {
                tagdata_struct output_struct = new();
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
                    test.GUID = GUID;
                    test.tag_data = tag_datas.Skip((int)struct_file_offset.Offset + (referenced_array_size * i)).Take(referenced_array_size).ToArray();
                    test.tag_block_refs = new();
                    test.tag_resource_refs = new();

                    // WE NEED TO DO THE OFFSET, BECAUSE TAGBLOCKS ARE STORED IN THE SAME TAGDATA REGION
                    // ELSE ALL THE TAGBLOCKS WILL HAVE THE SAME CONTENTS (or same counts)
                    process_literal_struct(currentStruct, struct_index, ref test, (ulong)(referenced_array_size * i));

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
            private void process_literal_struct(XmlNode structparent, uint struct_index, ref thing append_to, ulong current_offset = 0)
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


                    ulong relative_offset = (ulong)Convert.ToInt32(currentParam.Attributes["Offset"].Value, 16) + current_offset;

                    // write the node, then write attributes
                    switch (currentParam.Name)
                    {
                        case "_38":
                            { // _field_struct - 0byte
                                string xml_guid = "_" + currentParam.Attributes["GUID"].Value;
                                XmlNode struct_node = reference_root.SelectSingleNode(xml_guid);
                                process_literal_struct(struct_node, struct_index, ref append_to, relative_offset);
                            }
                            break;
                        case "_39":
                            { // _field_array - 0byte
                                string xml_guid = "_" + currentParam.Attributes["GUID"].Value;
                                int array_count = Convert.ToInt32(currentParam.Attributes["Count"].Value);
                                XmlNode struct_node = reference_root.SelectSingleNode(xml_guid);
                                int referenced_array_size = Convert.ToInt32(struct_node.Attributes["Size"].Value);
                                for (int array_ind = 0; array_ind < array_count; array_ind++)
                                    process_literal_struct(struct_node, struct_index, ref append_to, relative_offset + (ulong)(referenced_array_size * array_ind));
                            }
                            break;
                        case "_40":
                            { // _field_tag_block - 20byte
                              // read the count
                              // find the struct that this is referring to
                                string struct_guid = currentParam.Attributes["GUID"].Value;
                                ulong param_offset = relative_offset + data_blocks[tag_structs[struct_index].TargetIndex].Offset;
                                int tagblock_count = read_int_from_array_at(data_blocks[tag_structs[struct_index].TargetIndex].Section, (int)(param_offset + 16));
                                uint next_struct_index = struct_links[struct_index][(uint)relative_offset];
                                //tag_def_structure next_struct = tag_structs[];
                                append_to.tag_block_refs.Add(relative_offset, process_highlevel_struct(next_struct_index, struct_guid, tagblock_count));
                            }
                            break;
                        // we should also do one of these arry dictionary thingos for tag references _41
                        case "_42": // _field_data - 24byte
                            {   // for this, you're supposed to find the corresponding 'data_references' index, which will tell us which data block to read via the 'target_index'
                                // wow thats way too many steps
                                uint data_ref_index = data_links[struct_index][(uint)relative_offset];
                                data_reference data_header = data_references[data_ref_index];
                                if (data_header.TargetIndex != -1)
                                {
                                    data_block data_data = data_blocks[data_header.TargetIndex];
                                    byte[] data_bytes = return_referenced_byte_segment(data_data.Section).Skip((int)data_data.Offset).Take((int)data_data.Size).ToArray();
                                    append_to.tag_resource_refs.Add(relative_offset, data_bytes);
                                }
                                else append_to.tag_resource_refs.Add(relative_offset, new byte[0]);
                            }
                            break;
                        case "_43": // _field_resource - 16byte
                            // hmm, this section works basically exactly the same as tagblocks, except these refer to files outside of this file
                            {
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



            // this is how we'd access the root entire structure
            // 
            // this is how we would request a single parameter
            // 10
            // this is how we'd access entire structure of a tagblock
            // 10:0
            // this is how we'd access a param from a tagblock
            // 10:0,10

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
            [FieldOffset(0x34)] public uint    Unk_0x34; // new with infinite, cold be an enum of how to read the alignment bytes
                                
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
                                
            [FieldOffset(0x10)] public ushort  Type;           // "0 = Main Struct, 1 = Tag Block, 2 = Resource, 3 = Custom"
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
