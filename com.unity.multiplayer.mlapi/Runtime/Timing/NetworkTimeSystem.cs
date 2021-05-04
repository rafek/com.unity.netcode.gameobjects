using System;
using MLAPI.Configuration;
using UnityEngine;

namespace MLAPI.Timing
{
    public class NetworkTimeSystem
    {
        private INetworkTimeProvider m_NetworkTimeProvider;
        private int m_TickRate;
        private NetworkConfig m_Config;

        private NetworkTime m_PredictedTime;
        private NetworkTime m_ServerTime;

        /// <summary>
        /// Special value to indicate "No tick information"
        /// </summary>
        public const int NoTick = int.MinValue;

        /// <summary>
        /// The NetworkConfig used for this <see cref="NetworkTimeSystem"/>.
        /// </summary>
        public NetworkConfig Config => m_Config;

        /// <summary>
        /// Gets the tick of the last server snapshot which has been received.
        /// </summary>
        public NetworkTime LastReceivedServerSnapshotTick { get; internal set; }

        /// <summary>
        /// The current predicted time. This is the time at which predicted or client authoritative objects move. This value is accurate when called in Update or NetworkFixedUpdate but does not work correctly for FixedUpdate.
        /// </summary>
        public NetworkTime PredictedTime => m_PredictedTime;

        /// <summary>
        /// The current server time. This value is mostly used for internal purposes and to interpolate state received from the server. This value is accurate when called in Update or NetworkFixedUpdate but does not work correctly for FixedUpdate.
        /// </summary>
        public NetworkTime ServerTime => m_ServerTime;

        /// <summary>
        /// The TickRate of the time system. This is used to decide how often a fixed network tick is run.
        /// </summary>
        public int TickRate => m_TickRate;

        /// <summary>
        /// Creates a new instance of the <see cref="NetworkTimeSystem"/>.
        /// </summary>
        /// <param name="config">The network config</param>
        public NetworkTimeSystem(NetworkConfig config, bool isServer)
        {
            m_Config = config;
            m_TickRate = config.TickRate;

            if (isServer)
            {
                m_NetworkTimeProvider = new FixedNetworkTimeProvider();
            }
            else
            {
                m_NetworkTimeProvider = new DynamicNetworkTimeProvider(this);
            }
            
            m_PredictedTime = new NetworkTime(config.TickRate);
            m_ServerTime = new NetworkTime(config.TickRate);
        }

        /// <summary>
        /// Called each network loop update during the <see cref="NetworkUpdateStage.PreUpdate"/> to advance the network time.
        /// </summary>
        public void AdvanceNetworkTime(float deltaTime)
        {
            m_NetworkTimeProvider.AdvanceTime(ref m_PredictedTime, ref m_ServerTime, deltaTime);
        }

        /// <summary>
        /// Called on the client in the initial spawn packet to initialize the time with the correct server value.
        /// </summary>
        /// <param name="serverTick">The server tick at initialization time</param>
        public void InitializeClient(int serverTick)
        {
            LastReceivedServerSnapshotTick = new NetworkTime(TickRate, serverTick);

           m_ServerTime = new NetworkTime(TickRate, serverTick);

           // This should be overriden by the time provider but setting it in case it's not
           m_PredictedTime = new NetworkTime(TickRate, serverTick);

           m_NetworkTimeProvider.InitializeClient(ref m_PredictedTime, ref m_ServerTime);
        }
    }
}
