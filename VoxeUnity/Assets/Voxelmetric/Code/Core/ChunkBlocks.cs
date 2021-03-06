﻿using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using UnityEngine;
using Voxelmetric.Code.Common;
using Voxelmetric.Code.Core.StateManager;
using Voxelmetric.Code.Data_types;
using Voxelmetric.Code.Utilities;
using Voxelmetric.Code.VM;

namespace Voxelmetric.Code.Core
{
    public sealed class ChunkBlocks
    {
        public Chunk chunk { get; private set; }
        private readonly ChunkVolume blocks = new ChunkVolume();
        private Block[] m_blockTypes;

        //! Queue of setBlock operations to execute
        private readonly List<SetBlockContext> m_setBlockQueue = new List<SetBlockContext>();

        private byte[] receiveBuffer;
        private int receiveIndex;

        private long lastUpdateTimeGeometry;
        private long lastUpdateTimeCollider;
        private int rebuildMaskGeometry;
        private int rebuildMaskCollider;

        public readonly List<BlockPos> modifiedBlocks = new List<BlockPos>();
        public bool contentsInvalidated;

        private static byte[] emptyBytes;
        public static byte[] EmptyBytes
        {
            get
            {
                if (emptyBytes==null)
                    emptyBytes = new byte[16384]; // TODO: Validate whether this is fine
                return emptyBytes;
            }
        }

        //! Number of blocks which are not air (non-empty blocks)
        public int NonEmptyBlocks
        {
            get { return blocks.NonEmptyBlocks; }
        }

        public ChunkBlocks(Chunk chunk)
        {
            this.chunk = chunk;
        }

        public void Init()
        {
            m_blockTypes = chunk.world.blockProvider.BlockTypes;
        }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("contentsInvliadated=");
            sb.Append(contentsInvalidated.ToString());
            return sb.ToString();
        }

        public void Reset()
        {
            blocks.Clear();

            lastUpdateTimeGeometry = 0;
            lastUpdateTimeCollider = 0;
            rebuildMaskGeometry = -1;
            rebuildMaskCollider = -1;

            contentsInvalidated = true;

            modifiedBlocks.Clear();
        }

        public void Update()
        {
            ChunkStateManagerClient stateManager = (ChunkStateManagerClient)chunk.stateManager;

            if (m_setBlockQueue.Count>0)
            {
                if (rebuildMaskGeometry<0)
                    rebuildMaskGeometry = 0;
                if (rebuildMaskCollider<0)
                    rebuildMaskCollider = 0;

                // Modify blocks
                for (int j = 0; j<m_setBlockQueue.Count; j++)
                {
                    SetBlockContext context = m_setBlockQueue[j];

                    int x, y, z;
                    Helpers.GetChunkIndex3DFrom1D(context.Index, out x, out y, out z);

                    Vector3Int pos = new Vector3Int(x, y, z);
                    Vector3Int globalPos = pos+chunk.pos;

                    BlockData oldBlockData = blocks.Get(context.Index);

                    Block oldBlock = m_blockTypes[oldBlockData.Type];
                    Block newBlock = m_blockTypes[context.Block.Type];
                    oldBlock.OnDestroy(chunk, pos, globalPos);
                    newBlock.OnCreate(chunk, pos, globalPos);

                    blocks.Set(context.Index, context.Block);

                    if (context.SetBlockModified)
                    {
                        BlockModified(new BlockPos(x, y, z), globalPos, context.Block);

                        chunk.blocks.contentsInvalidated = true;
                    }

                    if (
                        // Only check neighbors if it is still needed
                        rebuildMaskGeometry==0x3f ||
                        // Only check neighbors when it is a change of a block on a chunk's edge
                        (((pos.x+1)&Env.ChunkMask)>1 &&
                         ((pos.y+1)&Env.ChunkMask)>1 &&
                         ((pos.z+1)&Env.ChunkMask)>1)
                        )
                        continue;

                    int cx = chunk.pos.x;
                    int cy = chunk.pos.y;
                    int cz = chunk.pos.z;

                    // If it is an edge position, notify neighbor as well
                    // Iterate over neighbors and decide which ones should be notified to rebuild
                    for (int i = 0; i<stateManager.Listeners.Length; i++)
                    {
                        ChunkEvent listener = stateManager.Listeners[i];
                        if (listener==null)
                            continue;

                        // No further checks needed once we know all neighbors need to be notified
                        if (rebuildMaskGeometry==0x3f)
                            break;

                        ChunkStateManagerClient listenerChunk = (ChunkStateManagerClient)listener;

                        int lx = listenerChunk.chunk.pos.x;
                        int ly = listenerChunk.chunk.pos.y;
                        int lz = listenerChunk.chunk.pos.z;

                        if ((ly==cy || lz==cz) &&
                            (
                                // Section to the left
                                ((pos.x==0) && (lx+Env.ChunkSize==cx)) ||
                                // Section to the right
                                ((pos.x==Env.ChunkMask) && (lx-Env.ChunkSize==cx))
                            ))
                            rebuildMaskGeometry = rebuildMaskGeometry|(1<<i);

                        if ((lx==cx || lz==cz) &&
                            (
                                // Section to the bottom
                                ((pos.y==0) && (ly+Env.ChunkSize==cy)) ||
                                // Section to the top
                                ((pos.y==Env.ChunkMask) && (ly-Env.ChunkSize==cy))
                            ))
                            rebuildMaskGeometry = rebuildMaskGeometry|(1<<i);

                        if ((ly==cy || lx==cx) &&
                            (
                                // Section to the back
                                ((pos.z==0) && (lz+Env.ChunkSize==cz)) ||
                                // Section to the front
                                ((pos.z==Env.ChunkMask) && (lz-Env.ChunkSize==cz))
                            ))
                            rebuildMaskGeometry = rebuildMaskGeometry|(1<<i);
                    }

                    rebuildMaskCollider |= rebuildMaskGeometry;
                }

                m_setBlockQueue.Clear();
            }

            long now = Globals.Watch.ElapsedMilliseconds;

            // Request a geometry update at most 10 times a second
            if (rebuildMaskGeometry>=0 && now-lastUpdateTimeGeometry>=100)
            {
                lastUpdateTimeGeometry = now;

                // Request rebuild on this chunk
                stateManager.RequestState(ChunkState.BuildVerticesNow);;

                // Notify neighbors that they need to rebuilt their geometry
                if (rebuildMaskGeometry>0)
                {
                    for (int j = 0; j<stateManager.Listeners.Length; j++)
                    {
                        ChunkStateManagerClient listener = (ChunkStateManagerClient)stateManager.Listeners[j];
                        if (listener!=null && ((rebuildMaskGeometry>>j)&1)!=0)
                        {
                            // Request rebuild on neighbor chunks
                            listener.RequestState(ChunkState.BuildVerticesNow);
                        }
                    }
                }

                rebuildMaskGeometry = -1;
            }

            // Request a collider update at most 4 times a second
            if (chunk.NeedsCollider && rebuildMaskCollider>=0 && now-lastUpdateTimeCollider>=250)
            {
                lastUpdateTimeCollider = now;

                // Request rebuild on this chunk
                stateManager.RequestState(ChunkState.BuildCollider);

                // Notify neighbors that they need to rebuilt their geometry
                if (rebuildMaskCollider > 0)
                {
                    for (int j = 0; j < stateManager.Listeners.Length; j++)
                    {
                        ChunkStateManagerClient listener = (ChunkStateManagerClient)stateManager.Listeners[j];
                        if (listener != null && ((rebuildMaskCollider >> j) & 1) != 0)
                        {
                            // Request rebuild on neighbor chunks
                            if (listener.chunk.NeedsCollider)
                                listener.RequestState(ChunkState.BuildCollider);
                        }
                    }
                }

                rebuildMaskCollider = -1;
            }
        }

        /// <summary>
        /// Returns block data from a position within the chunk
        /// </summary>
        /// <param name="pos">A local block position</param>
        /// <returns>The block at the position</returns>
        public BlockData Get(Vector3Int pos)
        {
            return blocks.Get(ref pos);
        }

        /// <summary>
        /// Returns block data from a position within the chunk
        /// </summary>
        /// <param name="index">Index to internal block buffer</param>
        /// <returns>The block at the position</returns>
        public BlockData Get(int index)
        {
            return blocks.Get(index);
        }

        /// <summary>
        /// Returns a block from a position within the chunk
        /// </summary>
        /// <param name="pos">A local block position</param>
        /// <returns>The block at the position</returns>
        public Block GetBlock(Vector3Int pos)
        {
            return m_blockTypes[blocks.Get(ref pos).Type];
        }

        /// <summary>
        /// Returns a block from a position within the chunk
        /// </summary>
        /// <param name="index">Index to internal block buffer</param>
        /// <returns>The block at the position</returns>
        public Block GetBlock(int index)
        {
            return m_blockTypes[blocks.Get(index).Type];
        }

        /// <summary>
        /// Sets the block at the given position
        /// </summary>
        /// <param name="pos">A local block position</param>
        /// <param name="blockData">A block to be placed on a given position</param>
        public void Set(Vector3Int pos, BlockData blockData)
        {
            blocks.Set(ref pos, blockData);
        }

        /// <summary>
        /// Sets the block at the given position
        /// </summary>
        /// <param name="index">Index to internal block buffer</param>
        /// <param name="blockData">A block to be placed on a given position</param>
        public void Set(int index, BlockData blockData)
        {
            blocks.Set(index, blockData);
        }

        /// <summary>
        /// Sets the block at the given position
        /// </summary>
        /// <param name="pos">A local block position</param>
        /// <param name="blockData">BlockData to place at the given location</param>
        /// <param name="setBlockModified">Set to true to mark chunk data as modified</param>
        public void Modify(Vector3Int pos, BlockData blockData, bool setBlockModified)
        {
            int index = Helpers.GetChunkIndex1DFrom3D(pos.x, pos.y, pos.z);

            // Nothing for us to do if block did not change
            BlockData oldBlockData = blocks.Get(index);
            if (oldBlockData.Type==blockData.Type)
                return;

            m_setBlockQueue.Add(new SetBlockContext(index, blockData, setBlockModified));
        }

        public void BlockModified(BlockPos blockPos, Vector3Int globalPos, BlockData blockData)
        {
            //If this is the server log the changed block so that it can be saved
            if (chunk.world.networking.isServer)
            {
                if (chunk.world.networking.allowConnections)
                    chunk.world.networking.server.BroadcastChange(globalPos, blockData, -1);

                if (!modifiedBlocks.Contains(blockPos))
                    modifiedBlocks.Add(blockPos);
            }
            else // if this is not the server send the change to the server to sync
            {
                chunk.world.networking.client.BroadcastChange(globalPos, blockData);
            }
        }

        private bool debugRecieve = false;

        private void InitializeChunkDataReceive(int index, int size)
        {
            receiveIndex = index;
            receiveBuffer = new byte[size];
        }

        public void ReceiveChunkData(byte[] buffer)
        {
            int index = BitConverter.ToInt32(buffer, VmServer.headerSize);
            int size = BitConverter.ToInt32(buffer, VmServer.headerSize+4);
            if (debugRecieve)
                Debug.Log("ChunkBlocks.ReceiveChunkData ("+Thread.CurrentThread.ManagedThreadId+"): "+chunk.pos
                          //+ ", buffer=" + buffer.Length
                          +", index="+index
                          +", size="+size);

            if (receiveBuffer==null)
                InitializeChunkDataReceive(index, size);

            TranscribeChunkData(buffer, VmServer.leaderSize);
        }

        private void TranscribeChunkData(byte[] buffer, int offset)
        {
            for (int o = offset; o<buffer.Length; o++)
            {
                receiveBuffer[receiveIndex] = buffer[o];
                receiveIndex++;

                if (receiveIndex==receiveBuffer.Length)
                {
                    if (debugRecieve)
                        Debug.Log("ChunkBlocks.TranscribeChunkData ("+Thread.CurrentThread.ManagedThreadId+"): "+
                                  chunk.pos
                                  +", receiveIndex="+receiveIndex);

                    FinishChunkDataReceive();
                    return;
                }
            }
        }

        private void FinishChunkDataReceive()
        {
            GenerateContentsFromBytes();

            ChunkStateManagerClient stateManager = (ChunkStateManagerClient)chunk.stateManager;
            ChunkStateManagerClient.OnGenerateDataOverNetworkDone(stateManager);

            receiveBuffer = null;
            receiveIndex = 0;

            if (debugRecieve)
                Debug.Log("ChunkBlocks.FinishChunkDataReceive ("+Thread.CurrentThread.ManagedThreadId+"): "+chunk.pos);
        }

        public byte[] ToBytes()
        {
            List<byte> buffer = new List<byte>();
            BlockData blockData;
            BlockData lastBlockData = new BlockData(1);

            byte[] data;
            short sameBlockCount = 0;
            int countIndex = 0;

            for (int y = 0; y<Env.ChunkSize; y++)
            {
                for (int z = 0; z<Env.ChunkSize; z++)
                {
                    for (int x = 0; x<Env.ChunkSize; x++)
                    {
                        int index = Helpers.GetChunkIndex1DFrom3D(x, y, z);
                        blockData = blocks.Get(index);

                        if (blockData.Equals(lastBlockData))
                        {
                            //if this is the same as the last block added increase the count
                            ++sameBlockCount;
                            byte[] shortAsBytes = BitConverter.GetBytes(sameBlockCount);
                            buffer[countIndex] = shortAsBytes[0];
                            buffer[countIndex+1] = shortAsBytes[1];
                        }
                        else
                        {
                            BlockData bd = new BlockData(blockData.Type);
                            data = bd.ToByteArray();

                            //Add 1 as a short (2 bytes)
                            countIndex = buffer.Count;
                            sameBlockCount = 1;
                            buffer.AddRange(BitConverter.GetBytes(1));
                            //Then add the block data
                            buffer.AddRange(data);

                            lastBlockData = blockData;
                        }

                    }
                }
            }

            return buffer.ToArray();
        }

        private void GenerateContentsFromBytes()
        {
            int i = 0;
            BlockData blockData = new BlockData(0);
            short blockCount = 0;

            for (int y = 0; y<Env.ChunkSize; y++)
            {
                for (int z = 0; z<Env.ChunkSize; z++)
                {
                    for (int x = 0; x<Env.ChunkSize; x++)
                    {
                        if (blockCount==0)
                        {
                            blockCount = BitConverter.ToInt16(receiveBuffer, i);
                            i += 2;

                            ushort type = BitConverter.ToUInt16(receiveBuffer, i);
                            blockData = new BlockData(type);
                            i += 2;
                            i += blockData.RestoreBlockData(receiveBuffer, i);
                        }

                        Set(new Vector3Int(x, y, z), blockData);
                        blockCount--;
                    }
                }
            }
        }
    }
}