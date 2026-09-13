using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using BfresLibrary.Switch.Core;
using BfresLibrary.Core;
using BfresLibrary;
using System.IO;

namespace BfresLibrary.Switch
{
    internal class VertexBufferParser
    {
        public static void Load(ResFileSwitchLoader loader, VertexBuffer vertexBuffer)
        {
            if (loader.ResFile.VersionMajor >= 9)
                vertexBuffer.Flags = loader.ReadUInt32();
            else
                loader.LoadHeaderBlock();

            vertexBuffer.Attributes = loader.LoadDictValues<VertexAttrib>();
            vertexBuffer.MemoryPool = loader.Load<MemoryPool>();
            long bufferArrayOffset = loader.ReadOffset();
            if (HasBufferPointerArray(loader.ResFile))
                loader.ReadOffset();
            long VertexBufferSizeOffset = loader.ReadOffset();
            long VertexStrideSizeOffset = loader.ReadOffset();
            long userPointer = loader.ReadInt64();
            uint memoryPoolOffset = loader.ReadUInt32();
            byte numVertexAttrib = loader.ReadByte();
            byte numBuffer = loader.ReadByte();
            ushort Idx = loader.ReadUInt16();
            vertexBuffer.VertexCount = loader.ReadUInt32();
            vertexBuffer.VertexSkinCount = loader.ReadByte();
            loader.ReadByte(); //reserved
            ushort alignment = loader.ReadUInt16();
            vertexBuffer.GPUBufferAlignent = HasAlignmentField(loader.ResFile) && alignment != 0 ?
                alignment : GetDefaultAlignment(loader.ResFile);

            var StrideArray = loader.LoadList<VertexBufferStride>(numBuffer, (uint)VertexStrideSizeOffset);
            var VertexBufferSizeArray = loader.LoadList<VertexBufferSize>(numBuffer, (uint)VertexBufferSizeOffset);

            // The runtime places each buffer after the previous one's size rounded up to the
            // alignment, starting at the vertex memory pool offset.
            vertexBuffer.Buffers = new List<Buffer>();
            long offset = memoryPoolOffset;
            for (int buff = 0; buff < numBuffer; buff++)
            {
                Buffer buffer = new Buffer();
                buffer.Data = new byte[1][];
                buffer.Stride = (ushort)StrideArray[buff].Stride;

                uint size = VertexBufferSizeArray[buff].Size;
                using (loader.TemporarySeek(loader.GetBufferDataOffset(vertexBuffer.MemoryPool) + offset, SeekOrigin.Begin))
                    buffer.Data[0] = loader.ReadBytes((int)size);
                offset += AlignUp(size, vertexBuffer.GPUBufferAlignent);
                vertexBuffer.Buffers.Add(buffer);
            }
        }

        public static void Save(ResFileSwitchSaver saver, VertexBuffer vertexBuffer)
        {
            if (saver.ResFile.VersionMajor >= 9)
                saver.Write(vertexBuffer.Flags);
            else
                saver.Seek(12);
            saver.SaveRelocateEntryToSection(saver.Position, 2, 1, 0, ResFileSwitchSaver.Section1, "FVTX");
            vertexBuffer.AttributeOffset = saver.SaveOffset();
            vertexBuffer.AttributeDictOffset = saver.SaveOffset();
            if (vertexBuffer.MemoryPool != null)
                saver.SaveRelocateEntryToSection(saver.Position, 1, 1, 0, ResFileSwitchSaver.Section4, "Vertex Memory pool");
            saver.SaveMemoryPoolPointer();

            bool hasPointerArray = HasBufferPointerArray(saver.ResFile);
            saver.SaveRelocateEntryToSection(saver.Position, hasPointerArray ? 4u : 3u, 1, 0, ResFileSwitchSaver.Section1, "Vertex buffer info");
            vertexBuffer.UnkBufferOffset = saver.SaveOffset();
            if (hasPointerArray)
                vertexBuffer.UnkBuffer2Offset = saver.SaveOffset();
            vertexBuffer.BufferSizeArrayOffset = saver.SaveOffset();
            vertexBuffer.StideArrayOffset = saver.SaveOffset();
            saver.Write(0L); //user pointer
            saver.Write(SetVertexBufferArrayOffset(vertexBuffer, saver)); //Buffer Offset
            saver.Write((byte)vertexBuffer.Attributes.Count);
            saver.Write((byte)vertexBuffer.Buffers.Count);
            saver.Write((ushort)saver.CurrentIndex);
            saver.Write(vertexBuffer.VertexCount);
            saver.Write(vertexBuffer.VertexSkinCount);
            saver.Write((byte)0); //reserved
            if (HasAlignmentField(saver.ResFile))
                saver.Write(vertexBuffer.GPUBufferAlignent);
            else
                saver.Write((ushort)0);
        }

        public static uint SetVertexBufferArrayOffset(VertexBuffer vertexBuffer, ResFileSaver saver)
        {
            if (saver.ExportedShape != null)
                return AlignUp(GetIndexBufferEnd(saver.ExportedShape.Meshes), GetAlignment(saver.ResFile, vertexBuffer));

            foreach (var entry in GetBufferLayout(saver.ResFile))
            {
                if (entry.VertexBuffer == vertexBuffer)
                    return entry.Offset;
            }
            return 0;
        }

        /// <summary>
        /// Computes where every vertex buffer of the file starts relative to the beginning of the buffer memory
        /// pool data, which holds all index buffers followed by all vertex buffers.
        /// </summary>
        internal static List<(VertexBuffer VertexBuffer, uint Offset)> GetBufferLayout(ResFile resFile)
        {
            var meshes = resFile.Models.Values.SelectMany(x => x.Shapes.Values).SelectMany(x => x.Meshes);
            uint position = GetIndexBufferEnd(meshes);

            var layout = new List<(VertexBuffer, uint)>();
            foreach (Model fmdl in resFile.Models.Values)
            {
                foreach (VertexBuffer vtx in fmdl.VertexBuffers)
                {
                    uint alignment = GetAlignment(resFile, vtx);
                    position = AlignUp(position, alignment);
                    layout.Add((vtx, position));
                    foreach (Buffer buff in vtx.Buffers)
                        position += AlignUp(buff.Size, alignment);
                }
            }
            return layout;
        }

        static uint GetIndexBufferEnd(IEnumerable<Mesh> meshes)
        {
            uint position = 0;
            foreach (Mesh msh in meshes)
                position = AlignUp(position, 8) + (uint)msh.Data.Length;
            return position;
        }

        /// <summary>
        /// Gets the alignment the runtime uses between the buffers of the given vertex buffer.
        /// </summary>
        internal static ushort GetAlignment(ResFile resFile, VertexBuffer vertexBuffer)
        {
            if (HasAlignmentField(resFile) && vertexBuffer.GPUBufferAlignent != 0)
                return vertexBuffer.GPUBufferAlignent;
            return GetDefaultAlignment(resFile);
        }

        /// <summary>
        /// Runtimes before 5.0 align vertex buffers to 64 bytes and later ones to 8.
        /// </summary>
        static ushort GetDefaultAlignment(ResFile resFile) => (ushort)(resFile.VersionMajor < 5 ? 64 : 8);

        /// <summary>
        /// ResVertexData gained a vertex buffer alignment field in 9.1, replacing reserved bytes.
        /// </summary>
        static bool HasAlignmentField(ResFile resFile) =>
            resFile.VersionMajor > 9 || (resFile.VersionMajor == 9 && resFile.VersionMinor >= 1);

        /// <summary>
        /// ResVertexData has no vertex buffer pointer array before 3.0.
        /// </summary>
        internal static bool HasBufferPointerArray(ResFile resFile) => resFile.VersionMajor >= 3;

        static uint AlignUp(uint value, uint alignment) =>
            alignment <= 1 ? value : (value + alignment - 1) / alignment * alignment;
    }
}
