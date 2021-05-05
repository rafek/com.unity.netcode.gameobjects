using System;
using System.Collections.Generic;
using System.IO;
using MLAPI.Configuration;
using MLAPI.Messaging;
using MLAPI.NetworkVariable;
using MLAPI.Serialization;
using MLAPI.Serialization.Pooled;
using MLAPI.Transports;
using UnityEngine;
using UnityEngine.UIElements;

namespace MLAPI
{
    internal struct Key
    {
        public ulong NetworkObjectId; // the NetworkObjectId of the owning GameObject
        public ushort BehaviourIndex; // the index of the behaviour in this GameObject
        public ushort VariableIndex; // the index of the variable in this NetworkBehaviour
    }
    internal struct Entry
    {
        public Key Key;
        public ushort TickWritten; // the network tick at which this variable was set
        public ushort Position; // the offset in our Buffer
        public ushort Length; // the length of the data in Buffer
        public bool Fresh; // indicates entries that were just received

        public const int NotFound = -1;
    }

    internal class EntryBlock
    {
        private const int k_MaxVariables = 64;
        private const int k_BufferSize = 20000;

        public byte[] Buffer = new byte[k_BufferSize];
        public int Beg = 0; // todo: clarify usage. Right now, this is the beginning of the _free_ space.
        public int End = 0;

        public Entry[] Entries = new Entry[k_MaxVariables];
        public int LastEntry = 0;
        public MemoryStream Stream;

        public EntryBlock()
        {
            Stream = new MemoryStream(Buffer, 0, k_BufferSize);
        }

        public int Find(Key key)
        {
            for (int i = 0; i < LastEntry; i++)
            {
                if (Entries[i].Key.NetworkObjectId == key.NetworkObjectId &&
                    Entries[i].Key.BehaviourIndex == key.BehaviourIndex &&
                    Entries[i].Key.VariableIndex == key.VariableIndex)
                {
                    return i;
                }
            }

            return Entry.NotFound;
        }

        public int AddEntry(ulong networkObjectId, int behaviourIndex, int variableIndex)
        {
            var pos = LastEntry++;
            var entry = Entries[pos];

            entry.Key.NetworkObjectId = networkObjectId;
            entry.Key.BehaviourIndex = (ushort)behaviourIndex;
            entry.Key.VariableIndex = (ushort)variableIndex;
            entry.TickWritten = 0;
            entry.Position = 0;
            entry.Length = 0;
            entry.Fresh = false;
            Entries[pos] = entry;

            return pos;
        }

        public void AllocateEntry(ref Entry entry, long size)
        {
            // todo: deal with free space
            // todo: deal with full buffer

            entry.Position = (ushort)Beg;
            entry.Length = (ushort)size;
            Beg += (int)size;
        }
    }

    public class SnapshotSystem : INetworkUpdateSystem, IDisposable
    {
        private EntryBlock m_Snapshot = new EntryBlock();
        private EntryBlock m_ReceivedSnapshot = new EntryBlock();
        private NetworkManager m_NetworkManager = NetworkManager.Singleton;

        public SnapshotSystem()
        {
            this.RegisterNetworkUpdate(NetworkUpdateStage.EarlyUpdate);
        }

        public void Dispose()
        {
            this.UnregisterNetworkUpdate(NetworkUpdateStage.EarlyUpdate);
        }

        public void NetworkUpdate(NetworkUpdateStage updateStage)
        {
            if (updateStage == NetworkUpdateStage.EarlyUpdate)
            {
                if (m_NetworkManager.IsServer)
                {
                    for (int i = 0; i < m_NetworkManager.ConnectedClientsList.Count; i++)
                    {
                        var clientId = m_NetworkManager.ConnectedClientsList[i].ClientId;
                        SendSnapshot(clientId);
                    }
                }
                else
                {
                    SendSnapshot(m_NetworkManager.ServerClientId);
                }

                DebugDisplayStore(m_Snapshot, "Entries");
                DebugDisplayStore(m_ReceivedSnapshot, "Received Entries");
            }
        }

        private void SendSnapshot(ulong clientId)
        {
            // Send the entry index and the buffer where the variables are serialized
            using (var buffer = PooledNetworkBuffer.Get())
            {
                WriteIndex(buffer);
                WriteBuffer(buffer);

                m_NetworkManager.MessageSender.Send(clientId, NetworkConstants.SNAPSHOT_DATA,
                    NetworkChannel.SnapshotExchange, buffer);
                buffer.Dispose();
            }
        }

        private void WriteIndex(NetworkBuffer buffer)
        {
            using (var writer = PooledNetworkWriter.Get(buffer))
            {
                writer.WriteInt16((short)m_Snapshot.LastEntry);
                for (var i = 0; i < m_Snapshot.LastEntry; i++)
                {
                    WriteEntry(writer, in m_Snapshot.Entries[i]);
                }
            }
        }

        private void ReadIndex(NetworkReader reader)
        {
            Entry entry;
            short entries = reader.ReadInt16();

            for (var i = 0; i < entries; i++)
            {
                entry = ReadEntry(reader);
                entry.Fresh = true;

                int pos = m_ReceivedSnapshot.Find(entry.Key);
                if (pos == Entry.NotFound)
                {
                    pos = m_ReceivedSnapshot.AddEntry(entry.Key.NetworkObjectId, entry.Key.BehaviourIndex,
                        entry.Key.VariableIndex);
                }

                if (m_ReceivedSnapshot.Entries[pos].Length < entry.Length)
                {
                    m_ReceivedSnapshot.AllocateEntry(ref entry, entry.Length);
                }

                m_ReceivedSnapshot.Entries[pos] = entry;
            }
        }

        private void WriteEntry(NetworkWriter writer, in Entry entry)
        {
            writer.WriteUInt64(entry.Key.NetworkObjectId);
            writer.WriteUInt16(entry.Key.BehaviourIndex);
            writer.WriteUInt16(entry.Key.VariableIndex);
            writer.WriteUInt16(entry.TickWritten);
            writer.WriteUInt16(entry.Position);
            writer.WriteUInt16(entry.Length);
        }

        private Entry ReadEntry(NetworkReader reader)
        {
            Entry entry;
            entry.Key.NetworkObjectId = reader.ReadUInt64();
            entry.Key.BehaviourIndex = reader.ReadUInt16();
            entry.Key.VariableIndex = reader.ReadUInt16();
            entry.TickWritten = reader.ReadUInt16();
            entry.Position = reader.ReadUInt16();
            entry.Length = reader.ReadUInt16();
            entry.Fresh = false;

            return entry;
        }

        private void WriteBuffer(NetworkBuffer buffer)
        {
            using (var writer = PooledNetworkWriter.Get(buffer))
            {
                writer.WriteUInt16((ushort)m_Snapshot.Beg);
            }

            // todo: this sends the whole buffer
            // we'll need to build a per-client list
            buffer.Write(m_Snapshot.Buffer, 0, m_Snapshot.Beg);
        }

        private void ReadBuffer(NetworkReader reader, Stream snapshotStream)
        {
            int snapshotSize = reader.ReadUInt16();

            snapshotStream.Read(m_ReceivedSnapshot.Buffer, 0, snapshotSize);

            for (var i = 0; i < m_ReceivedSnapshot.LastEntry; i++)
            {
                if (m_ReceivedSnapshot.Entries[i].Fresh && m_ReceivedSnapshot.Entries[i].TickWritten > 0)
                {
                    var nv = FindNetworkVar(m_ReceivedSnapshot.Entries[i].Key);

                    m_ReceivedSnapshot.Stream.Seek(m_ReceivedSnapshot.Entries[i].Position, SeekOrigin.Begin);

                    // todo: Review whether tick still belong in netvar or in the snapshot table.
                    nv.ReadDelta(m_ReceivedSnapshot.Stream, m_NetworkManager.IsServer,
                        m_NetworkManager.NetworkTickSystem.GetTick(), m_ReceivedSnapshot.Entries[i].TickWritten);
                }

                m_ReceivedSnapshot.Entries[i].Fresh = false;
            }
        }

        public void Store(ulong networkObjectId, int behaviourIndex, int variableIndex, INetworkVariable networkVariable)
        {
            Key k;
            k.NetworkObjectId = networkObjectId;
            k.BehaviourIndex = (ushort)behaviourIndex;
            k.VariableIndex = (ushort)variableIndex;

            int pos = m_Snapshot.Find(k);
            if (pos == Entry.NotFound)
            {
                pos = m_Snapshot.AddEntry(networkObjectId, behaviourIndex, variableIndex);
            }

            // write var into buffer, possibly adjusting entry's position and length
            using (var varBuffer = PooledNetworkBuffer.Get())
            {
                networkVariable.WriteDelta(varBuffer);
                if (varBuffer.Length > m_Snapshot.Entries[pos].Length)
                {
                    // allocate this Entry's buffer
                    m_Snapshot.AllocateEntry(ref m_Snapshot.Entries[pos], varBuffer.Length);
                }

                m_Snapshot.Entries[pos].TickWritten = m_NetworkManager.NetworkTickSystem.GetTick();
                // Copy the serialized NetworkVariable into our buffer
                Buffer.BlockCopy(varBuffer.GetBuffer(), 0, m_Snapshot.Buffer, m_Snapshot.Entries[pos].Position, (int)varBuffer.Length);
            }
        }

        public void ReadSnapshot(Stream snapshotStream)
        {
            using (var reader = PooledNetworkReader.Get(snapshotStream))
            {
                ReadIndex(reader);
                ReadBuffer(reader, snapshotStream);
            }
        }

        private INetworkVariable FindNetworkVar(Key key)
        {
            var spawnedObjects = m_NetworkManager.SpawnManager.SpawnedObjects;

            if (spawnedObjects.ContainsKey(key.NetworkObjectId))
            {
                var behaviour = spawnedObjects[key.NetworkObjectId]
                    .GetNetworkBehaviourAtOrderIndex(key.BehaviourIndex);
                return behaviour.NetworkVariableFields[key.VariableIndex];
            }

            return null;
        }

        private void DebugDisplayStore(EntryBlock block, string name)
        {
            string table = "=== Snapshot table === " + name + " ===\n";
            for (int i = 0; i < block.LastEntry; i++)
            {
                table += string.Format("NetworkObject {0}:{1}:{2} range [{3}, {4}] ", block.Entries[i].Key.NetworkObjectId, block.Entries[i].Key.BehaviourIndex,
                    block.Entries[i].Key.VariableIndex, block.Entries[i].Position, block.Entries[i].Position + block.Entries[i].Length);

                for (int j = 0; j < block.Entries[i].Length && j < 4; j++)
                {
                    table += block.Buffer[block.Entries[i].Position + j].ToString("X2") + " ";
                }

                table += "\n";
            }
            Debug.Log(table);
        }
    }
}
