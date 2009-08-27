/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSim Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Reflection;
using System.Threading;
using System.Xml;
using System.Net;
using System.Text;
using System.IO;
using System.IO.Compression;
using log4net;
using OpenMetaverse;
using OpenMetaverse.Packets;
using OpenSim.Framework;
using OpenSim.Framework.Statistics;
using OpenSim.Framework.Servers;

using AssetRequestQueue = OpenSim.Framework.BlockingQueue<System.Collections.Generic.List<OpenSim.Interop.Cache.InteropCache.AssetRequest>>;
using DeferRequestQueue = OpenSim.Framework.BlockingQueue<OpenSim.Interop.Cache.InteropCache.AssetRequestsList>;

// using ICache            = GlynnTucker.Cache.ICache;
// using SimpleMemoryCache = GlynnTucker.Cache.SimpleMemoryCache;
using ICache            = OpenSim.Interop.Cache.ICache;
using SimpleMemoryCache = OpenSim.Interop.Cache.Cache;

namespace OpenSim.Interop.Cache
{

    public class InteropCache : IAssetCache
    {

        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private static readonly byte SOURCE_UNKNOWN   = 0;
        private static readonly byte SOURCE_DIRECT    = 2;
        private static readonly byte SOURCE_INVENTORY = 3;

        /// <summary>
        /// The cache itself!
        /// </summary>

        private ICache m_memcache = new SimpleMemoryCache();

        /// <summary>
        /// Client asset requests which are waiting for asset server data.  This includes texture requests
        /// </summary>

        private Dictionary<UUID, List<AssetRequest>> RequestedAssets;

        /// <summary>
        /// Region asset requests which are waiting for asset server data. Duplicate requests are folded.
        /// </summary>

        private Dictionary<UUID, AssetRequestsList> RequestLists;

        /// <summary>
        /// Asset requests with data which are ready to be sent back to requesters.  This includes textures.
        /// The deferred queues are an intermediate pipe used to disconnect the network resolution from 
        /// local processing.
        /// </summary>

        private AssetRequestQueue AssetRequests;

        /// <summary>
        /// Region and Client requests that were not resolved locally.
        /// </summary>

        private DeferRequestQueue DeferredRegionRequests;
        private AssetRequestQueue DeferredClientRequests;

        /// <summary>
        /// The local server from which assets can be requested and to which assets are persisted.
        /// </summary>

        private IAssetServer m_assetServer;

        public IAssetServer AssetServer
        {
            get { return m_assetServer; }
        }

        /// <summary>
        /// The following fields implement and control the operation of the
        /// distributed asset serving stuff.
        /// </summary>

        internal List<RemoteAssetServer> RemoteServerList = new List<RemoteAssetServer>();
        internal static readonly bool DUMP   = false;
        internal static uint m_port = 9999;
        internal static BaseHttpServer m_httpServer;
        internal static string m_prefix   = "/rrs/assetserver";
        internal static string m_register = "/rrs/register";
        
        #region IPlugin

        /// <summary>
        /// The methods and properties in this section are needed to
        /// support the IPlugin interface. They cann all be overridden
        /// as needed by a derived class.
        /// </summary>

        public virtual string Name
        {
            get { return "OpenSim.Interop.Cache.InteropCache"; }
        }

        public virtual string Version
        {
            get { return "1.0"; }
        }

        public virtual void Initialise()
        {
        }

        // These variables control the number of worker
        // threads employed by the cache.

        private static int REGION_DEFER_MAX = 1;
        private static int CLIENT_DEFER_MAX = 1;
        private static int CLIENT_NORM_MAX  = 1;

        /// <summary>
        /// This initializer does the real startup. It is called by the
        /// Initialise scheduled by the PluginLoader.
        /// </summary>

        public virtual void Initialise(IAssetServer assetServer)
        {

            m_log.InfoFormat("[DCACHE] Identity [{0}/{1}]", Name, Version);

            ResetQueues();
 
            // Create and schedule the normal client request processing thread.

            for (int i=0; i<CLIENT_NORM_MAX; i++)
            {
                Thread thread = new Thread(RunAssetManager);
                thread.Name = "AssetCacheThread (Normal) ["+i+"]";
                thread.IsBackground = true;
                thread.Start();
                ThreadTracker.Add(thread);
            }

            // Create and schedule the deferred region request processing thread(s).

            for (int i=0; i<REGION_DEFER_MAX; i++)
            {
                Thread thread = new Thread(RunDeferredRegionHandler);
                thread.Name = "DeferredAssetThread (Region) ["+i+"]";
                thread.IsBackground = true;
                thread.Start();
                ThreadTracker.Add(thread);
            }

            // Create and schedule the deferred client request processing thread(s).

            for (int i=0; i<CLIENT_DEFER_MAX; i++)
            {
                Thread thread = new Thread(RunDeferredClientHandler);
                thread.Name = "DeferredAssetThread (Client) ["+i+"]";
                thread.IsBackground = true;
                thread.Start();
                ThreadTracker.Add(thread);
            }

            // Load the starting set of known asset servers
           
            string local = Util.GetLocalHost().ToString();

            try
            {
				string[] slist = File.ReadAllLines("interop.txt");

				foreach (string s in slist)
				{
					if (s != local)
					{
						string line = s.Trim();
					   if (!line.StartsWith("#"))
						{
							string[] parts = line.Split('=');
							switch (parts[0].Trim().ToLower())
							{
								case "localport" :
									m_log.InfoFormat("[DCACHE] Setting server listener port to {0}", s);
									m_port = Convert.ToUInt32(parts[1].Trim());
									break;
								case "prefix" :
									m_log.InfoFormat("[DCACHE] Setting asset server prefix to {0}", s);
									m_prefix = parts[1].Trim();
									break;
								case "register" :
									m_log.InfoFormat("[DCACHE] Setting registration prefix to {0}", s);
									m_register = parts[1].Trim();
									break;
								case "server" :
									m_log.InfoFormat("[DCACHE] Registering host {0}", s);
									parts = parts[1].Split(':');
									string port = Convert.ToString(m_port);
									string addr = String.Empty;
									bool haveport = (parts.Length == 2);
									if (parts.Length > 1) port = parts[1].Trim();
									if (parts.Length > 0) addr = parts[0].Trim();
									if (addr != String.Empty)
									{
										string prefix = m_prefix;
										if(haveport)
										{
											parts = port.Split('/');
											port  = parts[0].Trim();
											if (parts.Length > 1) prefix = parts[1].Trim();
										}
										AddRemoteServer(addr, port, prefix);
									}
									break;
								default :
									m_log.DebugFormat("[DCACHE] Unrecognized command {0}:{1}", 
										parts[0], parts[1]);
									break;
							}
						}
						else
						{
							m_log.DebugFormat("[DCACHE] Ignoring line {0}", s);
						}
					}
					else
						m_log.DebugFormat("[DCACHE] Ignoring local host {0}", s);
				}
            }
            catch(Exception e)
            {
                m_log.DebugFormat("[DCACHE] Failed to read configuration file. {0}", e.Message);
            }

            m_httpServer = new BaseHttpServer(m_port);
            m_httpServer.Start();

            m_httpServer.AddStreamHandler(new RestStreamHandler("GET", m_prefix,   RemoteRequest));
            m_httpServer.AddStreamHandler(new RestStreamHandler("GET", m_register, RegisterServer));

            // Cache asset server interface and register this class as the
            // correspondent.

            m_assetServer = assetServer;
            m_assetServer.SetReceiver(this);

        }

        public virtual void Initialise(ConfigSettings settings, IAssetServer assetServer)
        {
            Initialise(assetServer);
        }

        public InteropCache()
        {
        }

        public void Dispose()
        {
        }

        #endregion

        #region IAssetCache

        /// <summary>
        /// IAssetCache: Only get an asset if we already have it in the cache.
        /// </summary>
        /// <param name="assetId"></param>
        /// <param name="asset"></param>
        /// <returns>true if the asset was in the cache, false if it was not</returns>

        public bool TryGetCachedAsset(UUID assetId, out AssetBase asset)
        {
            /* GT implementation
            Object o;
            bool result  = m_memcache.TryGet(assetId, out o);
            asset = (AssetBase) o;
            return result;
            */
            return m_memcache.TryGet(assetId, out asset);
        }

        /// <summary>
        /// IAssetCache: Asynchronously retrieve an asset. If available in the cache, the
        /// callback routine is called directly. Otherwise the requests is
        /// queued to the asset server. Subsequent requests for assets that
        /// have already been requested, but not yet processed, are folded
        /// together.
        /// </summary>
        /// <param name="assetId"></param>
        /// <param name="callback">
        /// <param name="isTexture"></param>

        public void GetAsset(UUID assetId, AssetRequestCallback callback, bool isTexture)
        {
            m_log.DebugFormat("[DCACHE] GetAsset(A) {0}, texture = {1}", assetId, isTexture);
            DoGetAsset(assetId, callback, isTexture);
        }

        /// <summary>
        /// IAssetCache: Synchronously retrieve an asset. If the asset is not in the cache, a request will 
        /// be made to the persistent store to load it into the cache.
        /// </summary>
        /// <param name="assetId"></param>
        /// <param name="isTexture"></param>
        /// <returns>null if the asset could not be retrieved</returns>

        public AssetBase GetAsset(UUID assetId, bool isTexture)
        {
            m_log.DebugFormat("[DCACHE] GetAsset(S) {0}, texture = {1}", assetId, isTexture);
            SyncRequest sync = new SyncRequest();
            DoGetAsset(assetId, sync.Post, isTexture);
            return sync.Wait(3000);
        }

        /// <summary>
        /// Common asset get code.
        /// </summary>

        private void DoGetAsset(UUID assetId, AssetRequestCallback callback, bool isTexture)
        {

            AssetBase asset;

            if (TryGetCachedAsset(assetId, out asset))
            {
                // m_log.DebugFormat("[DCACHE] Found {0} in the cache", assetId);
                callback(assetId, asset);
            }

            else
            {
                // m_log.DebugFormat("[DCACHE] Queuing server request for {0}", assetId);
                NewAssetRequest req = new NewAssetRequest(callback, true);
                QueueNewAssetRequest(assetId, req, isTexture);
            }
        }

        private void QueueNewAssetRequest(UUID assetId, NewAssetRequest req, bool isTexture)
        {
            lock (RequestLists)
            {

                AssetRequestsList requestList;

                if (RequestLists.TryGetValue(assetId, out requestList))
                {
                    // m_log.DebugFormat("[DCACHE] Piggy-back duplicate request for {0}", assetId);
                    requestList.Requests.Add(req);
                }
                else
                {
                    // m_log.DebugFormat("[DCACHE] Create new request for {0}", assetId);
                    requestList = new AssetRequestsList();
                    requestList.assetId = assetId;
                    requestList.IsTexture = isTexture;
                    requestList.TimeRequested = DateTime.Now;
                    requestList.Requests.Add(req);
                    RequestLists.Add(assetId, requestList);
                    m_assetServer.RequestAsset(assetId, isTexture);
                }
            }
        }

        /// <summary>
        /// IAssetCache: Add an asset to both the persistent store and the cache.
        /// </summary>
        /// <param name="asset"></param>

        public void AddAsset(AssetBase asset)
        {

            m_log.DebugFormat("[DCACHE] AddAsset {0}", asset.FullID);

            if (!m_memcache.Contains(asset.FullID))
            {

                // m_log.InfoFormat("[DCACHE] Caching " + asset.FullID + " for 24 hours from last access");

                m_memcache.AddOrUpdate(asset.FullID, asset, TimeSpan.FromHours(24));

                if (!asset.Temporary || asset.Local)
                {
                    m_assetServer.StoreAsset(asset);
                }

            }

        }

        /// <summary>
        /// IAssetCache: Allows you to clear a specific asset by uuid out
        /// of the asset cache.  This is needed because the osdynamic
        /// texture code grows the asset cache without bounds.  The
        /// real solution here is a much better cache architecture, but
        /// this is a stop gap measure until we have such a thing.
        /// </summary>

        public void ExpireAsset(UUID assetId)
        {
 
            m_log.DebugFormat("[DCACHE] ExpireAsset {0}", assetId);

            // uuid is unique, so no need to worry about it showing up
            // in the 2 caches differently.  Also, locks are probably
            // needed in all of this, or move to synchronized non
            // generic forms for Dictionaries.

            if (m_memcache.Contains(assetId))
            {
                m_memcache.Remove(assetId);
            }

        }

        /// <summary>
        /// IAssetCache: Handle an asset request from the client.  The result will be sent back asynchronously.
        /// </summary>
        /// <param name="userInfo"></param>
        /// <param name="transferRequest"></param>

        public void AddAssetRequest(IClientAPI userInfo, TransferRequestPacket transferRequest)
        {

            UUID requestID = UUID.Zero;
            byte source = SOURCE_UNKNOWN;

            if (transferRequest.TransferInfo.SourceType == SOURCE_DIRECT)
            {
                requestID = new UUID(transferRequest.TransferInfo.Params, 0);
                source = SOURCE_DIRECT;
            }
            else if (transferRequest.TransferInfo.SourceType == SOURCE_INVENTORY)
            {
                requestID = new UUID(transferRequest.TransferInfo.Params, 80);
                source = SOURCE_INVENTORY;
            }

            m_log.DebugFormat("[DCACHE] AddAssetRequest ENTRY <{0}>", requestID);

            if (source == SOURCE_UNKNOWN)
            {
                m_log.WarnFormat("[DCACHE] Unrecognized asset type : {0}", transferRequest.TransferInfo.SourceType);
                return;
            }

            //check to see if asset is in local cache, if not we need to request it from asset server.
            //m_log.DebugFormat("asset request " + requestID);
            if (!m_memcache.Contains(requestID))
            {

                //
                // Queue a request to the asset server. This is an
                // asynchronous operation, so initialize the request
                // and add it to the list of pending server 
                // requests and return directly. If such a request
                // is already pending, then turn this pass into a
                // no-operation.

                if (!RequestedAssets.ContainsKey(requestID))
                {
                    AssetRequest request = new AssetRequest();
                    List<AssetRequest> requests = new List<AssetRequest>();

                    request.RequestUser = userInfo;
                    request.RequestAssetID = requestID;
                    request.TransferRequestID = transferRequest.TransferInfo.TransferID;
                    request.AssetRequestSource = source;
                    request.Params = transferRequest.TransferInfo.Params;
                    requests.Add(request);

                   lock (RequestedAssets) RequestedAssets.Add(requestID, requests);

                    m_assetServer.RequestAsset(requestID, false);
                }
                else
                {
                    AssetRequest request = new AssetRequest();

                    request.RequestUser = userInfo;
                    request.RequestAssetID = requestID;
                    request.TransferRequestID = transferRequest.TransferInfo.TransferID;
                    request.AssetRequestSource = source;
                    request.Params = transferRequest.TransferInfo.Params;
                    
                   lock (RequestedAssets) RequestedAssets[requestID].Add(request);
                }

                return;

            }

            // It has an entry in our cache

            AssetBase asset = /* (AssetBase) */ m_memcache[requestID];

            // Scripts cannot be retrieved by direct request
            if (transferRequest.TransferInfo.SourceType == 2 && asset.Type == 10)
                return;

            // The asset is known to exist and is in our cache, so queue it back
            // for return to the requestor.

            AssetRequest req = new AssetRequest();
            List<AssetRequest> reqs = new List<AssetRequest>();
            req.RequestUser = userInfo;
            req.RequestAssetID = requestID;
            req.TransferRequestID = transferRequest.TransferInfo.TransferID;
            req.AssetRequestSource = source;
            req.Params = transferRequest.TransferInfo.Params;
            req.AssetInf = new AssetInfo(asset);
            req.NumPackets = CalculateNumPackets(asset.Data);
            reqs.Add(req);
            AssetRequests.Enqueue(reqs);

        }

        /// <summary>
        /// IAssetCache: Report statistical data.
        /// </summary>

        public void ShowState()
        {
            m_log.InfoFormat("Memcache:{0}   RequestLists:{1}",
                m_memcache.Count,
                RequestLists.Count);
        }

        /// <summary>
        /// IAssetCache: Clear the asset cache.
        /// </summary>

        public void Clear()
        {
            m_log.InfoFormat("[DCACHE] Clearing Asset cache");

            if (StatsManager.SimExtraStats != null)
                StatsManager.SimExtraStats.ClearAssetCacheStatistics();

            // Not supported by the GT cache?
            m_memcache.Clear();

            ResetQueues();

        }

        #endregion

        #region IAssetReceiver

        /// <summary>
        /// IAssetReceiver:
        /// This method is called when a previously queued asset request
        /// has been successfully completed by the asset server.
        /// </summary>

        public virtual void AssetReceived(AssetBase asset, bool IsTexture)
        {

            m_log.DebugFormat("[DCACHE] AssetReceived {0}, texture = {1}", asset.FullID, IsTexture);

            AssetRequestsList reqList;

            // It's possible that the request has been already 
            // satisfied, so check that it is still missing.

            if (!m_memcache.Contains(asset.FullID))
            {

                m_log.DebugFormat("[DCACHE] Adding {0} to cache", asset.FullID);

                m_memcache.AddOrUpdate(asset.FullID, asset, TimeSpan.FromHours(24));

            }

            // Check if there is an associated client request

            if (RequestedAssets.ContainsKey(asset.FullID))
            {

                m_log.DebugFormat("[DCACHE] Notifying client that {0} is available", asset.FullID);

                AssetInfo assetInf = new AssetInfo(asset);
                List<AssetRequest> reqs;

                lock (RequestedAssets)
                {
                    if (RequestedAssets.ContainsKey(assetInf.FullID))
                    {
                        reqs = RequestedAssets[assetInf.FullID];
                        RequestedAssets.Remove(assetInf.FullID);
                        m_log.DebugFormat("[DCACHE] Removed pending client request for {0}", asset.FullID);
                    }
                    else
                    {
                        reqs = null;
                        m_log.DebugFormat("[DCACHE] Pending client request for {0} disappeared", asset.FullID);
                    }
                }

                if (reqs != null)
                {
                   foreach (AssetRequest req in reqs)
                    if (!(req.AssetRequestSource == 2 && assetInf.Type == 10))
                    {
                        req.AssetInf     = assetInf;
                        req.NumPackets   = CalculateNumPackets(assetInf.Data);
                    }
                    AssetRequests.Enqueue(reqs);
                }
            }

            // Find the requestor list for this request.

            lock (RequestLists)
            {
                if (RequestLists.TryGetValue(asset.FullID, out reqList))
                    RequestLists.Remove(asset.FullID);
            }

            // If the list is not empty, notify everyone who requested the
            // asset.
 
            if (reqList != null)
            {
                m_log.DebugFormat("[DCACHE] Notifying region that {0} is available", asset.FullID);
                if (StatsManager.SimExtraStats != null)
                {
                    StatsManager.SimExtraStats.AddAssetRequestTimeAfterCacheMiss(DateTime.Now - reqList.TimeRequested);
                }

                lock (RequestLists)
                {
                    foreach (NewAssetRequest req in reqList.Requests)
                    {
                        req.Callback(asset.FullID, asset);
                    }
                }
            }

        }

        /// <summary>
        /// IAssetReceiver:
        /// This routine is called when a previously queued request could NOT
        /// be satisfied by the asset server. But we do not want to block the
        /// asset server's normal progress, so we re-queue the request for
        /// processing by the distributed resolver.
        /// </summary>

        public virtual void AssetNotFound(UUID assetId, bool IsTexture)
        {

            m_log.DebugFormat("[DCACHE] AssetNotFound {0}", assetId);

            // Check if there is an associated client request

            if (RequestedAssets.ContainsKey(assetId))
            {

                m_log.DebugFormat("[DCACHE] AssetNotFound Client notification for {0}", assetId);
                List<AssetRequest> reqs;

                lock (RequestedAssets)
                {
                    if (RequestedAssets.ContainsKey(assetId))
                    {
                        reqs = RequestedAssets[assetId];
                        RequestedAssets.Remove(assetId);
                    }
                    else
                    {
                        reqs = null;
                    }
                }
                if (reqs != null)
                {
                    DeferredClientRequests.Enqueue(reqs);
                }
            }

            // Find the requestor list for this request.

            lock (RequestLists)
            {
                if (RequestLists.ContainsKey(assetId))
                {
                    m_log.DebugFormat("[DCACHE] AssetNotFound Region notification for {0}", assetId);
                    AssetRequestsList freq = RequestLists[assetId];
                    RequestLists.Remove(assetId);
                    DeferredRegionRequests.Enqueue(freq);
                }
            }

        }

        #endregion

        #region Remote web interface

        // This is WAY too promiscuous, but will suffice for initial testing

        private string RegisterServer(string body, string path, string parms, OSHttpRequest req, OSHttpResponse rsp)
        {
            NameValueCollection qstr = req.QueryString;

            m_log.DebugFormat("[DCACHE] Register http://{0}:{1}{2}({3})",
                req.Headers["remote_addr"], req.Headers["remote_port"], path, parms);

            string ipa  = getValue(qstr, "ip",   String.Empty);
            string port = getValue(qstr, "port", String.Empty);

            AddRemoteServer(ipa, port, m_prefix);

            return String.Empty;

        }

        private string RemoteRequest(string body, string path, string parms, OSHttpRequest req, OSHttpResponse rsp)
        {

            string response = String.Empty;
            NameValueCollection qstr = req.QueryString;
            NameValueCollection hdrs = req.Headers;

            m_log.DebugFormat("[DCACHE] RemoteRequest http://{0}:{1}{2}({3})",
                req.Headers["remote_addr"], req.Headers["remote_port"], path, parms);

            // Remember all who try to talk to us ...

            if (!FindRemoteServer(req.Headers["remote_addr"], "9999"))
                AddRemoteServer(req.Headers["remote_addr"], "9999", m_prefix);
            
            if (DUMP)
            {
                foreach (string key in qstr.AllKeys)
                {
                   m_log.DebugFormat("[DCACHE] Query: {0:-10}:{1}", key, qstr.Get(key));
                }
                foreach (string key in hdrs.AllKeys)
                {
                   m_log.DebugFormat("[DCACHE] Header: {0:-10}:{1}", key, hdrs.Get(key));
                }
            }

            string assetid = getValue(qstr, "asset", String.Empty);
            bool   texture = Convert.ToBoolean(getValue(qstr, "texture", "false"));
            AssetBase asset = null;

            if (assetid != String.Empty)
            {
                UUID assetId = new UUID(assetid);
                m_log.DebugFormat("[DCACHE] GetAsset(R) {0}", assetId);
                if (!TryGetCachedAsset(assetId, out asset))
                {
                    SyncRequest sync = new SyncRequest();
                    NewAssetRequest rq = new NewAssetRequest(sync.Post, false);
                    QueueNewAssetRequest(assetId, rq, texture);
                    asset = sync.Wait(3000);
                }
            }

            if (asset == null)
            {
                rsp.StatusCode = 404;
                rsp.StatusDescription = "Asset not found";
            }
            else
            {
                response = String.Format("<body><asset>{0}</asset></body>", 
                      Convert.ToBase64String(asset.Data));
            }

            return response;

        }

        private string getValue(NameValueCollection coll, string key, string dft)
        {
            try
            {
                return coll[key];
            }
            catch (Exception)
            {
                return dft;
            }
        }

        #endregion

        #region Queue Management

        /// <summary>
        /// Process the asset queue which sends packets directly back to the client.
        /// Elements are placed on this queue whenever they are satisfied by the
        /// local asset server database.
        /// </summary>

        private void ProcessAssetQueue()
        {

            m_log.DebugFormat("[DCACHE] ProcessAssetQueue thread starting");

            while (true)
            {
                List<AssetRequest> reqs  = AssetRequests.Dequeue();
                foreach (AssetRequest req in reqs)
                {
                    AssetRequestToClient req2 = new AssetRequestToClient();
                    req2.AssetInf = req.AssetInf;
                    req2.AssetRequestSource = req.AssetRequestSource;
                    req2.DataPointer = req.DataPointer;
                    req2.DiscardLevel = req.DiscardLevel;
                    req2.ImageInfo = req.ImageInfo;
                    req2.IsTextureRequest = req.IsTextureRequest;
                    req2.NumPackets = req.NumPackets;
                    req2.PacketCounter = req.PacketCounter;
                    req2.Params = req.Params;
                    req2.RequestAssetID  = req.RequestAssetID;
                    req2.TransferRequestID = req.TransferRequestID;
                    req.RequestUser.SendAsset(req2);
                }
            }

        }

        /// <summary>
        /// This routine handles elements that are placed on the
        /// deferred region request queue, as a result of the
        /// asset not being resolved locally.
        /// </summary>

        private void ProcessDeferredRegionRequests()
        {

            m_log.DebugFormat("[DCACHE] ProcessDeferredRegionRequests thread starting");

            while (true)
            {

                AssetRequestsList reqList = DeferredRegionRequests.Dequeue();

                // If any of the entries are marked as non-retriable
                // then notify the requestor immediately.

                NewAssetRequest[] requests = reqList.Requests.ToArray();

                foreach (NewAssetRequest r in requests)
                {
                    if (!r.Retry)
                    {
                        reqList.Requests.Remove(r);
                        r.Callback(reqList.assetId, null);
                    }
                }

                // If there are any requestors left, try elsewhere

                if (reqList.Requests.Count > 0)
                {
                    AssetBase asset = lookElsewhere(reqList.assetId, reqList.IsTexture);

                    // Notify ALL of the remaining requestors regardless of outcome.

                    requests = reqList.Requests.ToArray();
                    foreach (NewAssetRequest req in requests)
                    {
                        req.Callback(reqList.assetId, asset);
                    }
                }

                m_log.DebugFormat("[DCACHE] ProcessDeferredRegionRequests Processing complete for {0}",
                        reqList.assetId);

            }

        }

        /// <summary>
        /// This routine handles elements that are placed on the
        /// deferred client request queue, as a result of the
        /// asset not being resolved locally.
        /// </summary>

        private void ProcessDeferredClientRequests()
        {

            m_log.DebugFormat("[DCACHE] ProcessDeferredClientRequests thread starting");

            while (true)
            {

                List<AssetRequest> reqs = DeferredClientRequests.Dequeue();
                AssetRequest req = reqs[0];

                m_log.DebugFormat("[DCACHE] ProcessDeferredClientRequests Processing started for {0}",
                        req.RequestAssetID);

                UUID assetId     = req.RequestAssetID;
                AssetInfo asset = (AssetInfo) lookElsewhere(assetId, req.IsTextureRequest);

                if (asset != null) 
                {
                   foreach (AssetRequest r in reqs)
                    {
                        r.AssetInf     = asset;
                        r.NumPackets   = CalculateNumPackets(asset.Data);
                    }
                    AssetRequests.Enqueue(reqs);
                }

                m_log.DebugFormat("[DCACHE] ProcessDeferredClientRequests Processing complete for {0}",
                        req.RequestAssetID);

            }

        }

        /// <summary>
        /// Process the asset queue which holds data which is packeted up and sent
        /// directly back to the client. The called function only returns in the 
        /// event of an exception.
        /// </summary>

        private void RunAssetManager()
        {

            while (true)
            {
                try
                {
                    ProcessAssetQueue();
                }
                catch (Exception e)
                {
                    m_log.ErrorFormat("[DCACHE] ProcessAssetQueue exception: " + e);
                }
            }
        }

        /// <summary>
        /// Process the queues of regional asset requests which could not be resolved 
        /// locally. The called function only returns in the event of an exception.
        /// </summary>

        private void RunDeferredRegionHandler()
        {

            while (true)
            {
                try
                {
                    ProcessDeferredRegionRequests();
                }
                catch (Exception e)
                {
                    m_log.ErrorFormat("[DCACHE] ProcessDeferredAssetQueue exception: " + e);
                }
            }
        }

        /// <summary>
        /// Process the queues of client asset requests which could not be resolved 
        /// locally. The called function only returns in the event of an exception.
        /// </summary>

        private void RunDeferredClientHandler()
        {

            while (true)
            {
                try
                {
                    ProcessDeferredClientRequests();
                }
                catch (Exception e)
                {
                    m_log.ErrorFormat("[DCACHE] ProcessDeferredClientQueue exception: " + e);
                }
            }
        }

        #endregion

        #region Web Services

        /// <summary>
        /// This is the common remote asset search method. It is called
        /// for both client and region requests. It tries all of the 
        /// remote servers it knows about and declares victory on the 
        /// first positive response. If it cannot be found, then a null
        /// reference is returned.
        /// </summary>

        private AssetBase lookElsewhere(UUID assetId, bool isTexture)
        {

            m_log.DebugFormat("[DCACHE] lookElsewhere {0}", assetId);

            AssetBase asset;

            RemoteAssetServer[] servers = RemoteServerList.ToArray();

            foreach (RemoteAssetServer server in servers)
            {
                if (server.GetAsset(assetId, isTexture, out asset))
                {
                    m_log.DebugFormat("[DCACHE] lookElsewhere resolved {0}", assetId);
                    m_memcache.AddOrUpdate(assetId, asset, TimeSpan.FromHours(24));
                    m_assetServer.StoreAsset(asset);
                    return asset;
                }
            }
            m_log.DebugFormat("[DCACHE] lookElsewhere abandoned {0}", assetId);
            return null;
        }

        /// <summary>
        /// This method hides the mechanics of maintaining the list
        /// of known servers.
        /// [AMW] A more sophisticated mechanism is needed.
        /// </summary>

        private void AddRemoteServer(string ipa, string port, string prefix)
        {
            RemoteServerList.Add(new RemoteAssetServer(ipa, port, prefix));
        }

        private bool FindRemoteServer(string ipa, string port)
        {
            foreach (RemoteAssetServer server in RemoteServerList)
                if (server.ipa == ipa && server.port == port)
                    return true;
            return false;
        }

        #endregion

        #region Supporting routines

        /// <summary>
        /// Calculate the number of packets required to send the asset to the client.
        /// </summary>
        /// <param name="data"></param>
        /// <returns></returns>

        private int CalculateNumPackets(byte[] data)
        {

            m_log.DebugFormat("[DCACHE] CalculateNumPackets ENTRY");

            const uint m_maxPacketSize = 600;
            int numPackets = 1;

            if (data.LongLength > m_maxPacketSize)
            {
                // over max number of bytes so split up file
                long restData = data.LongLength - m_maxPacketSize;
                int restPackets = (int)((restData + m_maxPacketSize - 1) / m_maxPacketSize);
                numPackets += restPackets;
            }

            m_log.DebugFormat("[DCACHE] CalculateNumPackets [{0}] EXIT", numPackets);

            return numPackets;
        }

        /// <summary>
        /// Reset request queues
        /// </summary>

        private void ResetQueues()
        {
            AssetRequests          = new AssetRequestQueue(); // inbound client requests
            RequestedAssets        = new Dictionary<UUID, List<AssetRequest>>();
            RequestLists           = new Dictionary<UUID, AssetRequestsList>();

            DeferredRegionRequests = new DeferRequestQueue(); // deferred region requests
            DeferredClientRequests = new AssetRequestQueue(); // deferred client requests
        }

        #endregion

        #region local classes

        /// <summary>
        /// This class represents a remote server. All communication
        /// is via this class.
        /// </summary>
 
        public class RemoteAssetServer
        {

            private string m_ipa;
            private string m_port;
            private string m_prefix;

            public string ipa    { get { return m_ipa;    } }
            public string port   { get { return m_port;   } }
            public string prefix { get { return m_prefix; } }
           
            public RemoteAssetServer(string ipa, string port, string prefix)
            {
                m_ipa    = ipa;
                m_port   = port;
                m_prefix = prefix;
            }

            private static readonly string GAP = "http://{0}:{1}{2}?asset={3}&texture={4}";

            public bool GetAsset(UUID assetid, bool isTexture, out AssetBase asset)
            {

                m_log.DebugFormat("[DCACHE] GetAsset(W) {0}", assetid);

                string requrl = String.Format(GAP, m_ipa, m_port, m_prefix, assetid, isTexture);
                XmlElement resp;

                if (webCall(requrl, out resp))
                {
                    string text;

                    if (XmlFind(resp, "body.asset", out text))
                    {
                        asset = new AssetBase();
                        asset.Data = Convert.FromBase64String(text);
                        return true;
                    }
                }
                
                asset = null;
                return false;

            }

            private bool webCall(string requrl, out XmlElement resp)
            {

                // Otherwise prepare the request

                m_log.DebugFormat("[DCACHE] Sending request <{0}>", requrl);

                HttpWebRequest  req = (HttpWebRequest)WebRequest.Create(requrl);            
                HttpWebResponse rsp = null;

                // We are sending just parameters, no content

                req.ContentLength = 0;

                // Send request and retrieve the response

                try
                {

                    rsp = (HttpWebResponse) req.GetResponse();

                    XmlTextReader rdr = new XmlTextReader(rsp.GetResponseStream());
                    XmlDocument   doc = new XmlDocument();
                    doc.Load(rdr);
                    rdr.Close();

                    // If we're debugging server responses, dump the whole
                    // load now

                    if (DUMP) XmlScanl(doc.DocumentElement,0);

                    resp = doc.DocumentElement;
                    return true;

                }
                catch (WebException w)
                {
                    m_log.DebugFormat("[DCACHE] Web exception: {0}", w.Message);
                }

                resp = null;
                return false;

            }

            /// <summary>
            /// The XmlScan routine is provided to aid in the
            /// debugging of exchanged packets. It works if the
            /// DUMP switch is set.
            /// </summary>

            private void XmlScanl(XmlElement e, int index)
            {
                if (e.HasChildNodes)
                {
                    m_log.DebugFormat("[DCACHE] <{0}>".PadLeft(index+14), e.Name);
                    XmlNodeList children = e.ChildNodes;
                    foreach (XmlNode node in children)
                       switch (node.NodeType)
                       {
                            case XmlNodeType.Element :
                                XmlScanl((XmlElement)node, index+1);
                                break;
                            case XmlNodeType.Text :
                                m_log.DebugFormat("[DCACHE] \"{0}\"".PadLeft(index+14), node.Value);
                                break;
                            default :
                                break;
                       }
                    m_log.DebugFormat("[DCACHE] </{0}>".PadLeft(index+15), e.Name);
                }
                else
                {
                    m_log.DebugFormat("[DCACHE] <{0}/>".PadLeft(index+15), e.Name);
                }
            }

            /// <summary>
            /// The Find method is passed an element whose
            /// inner text is scanned in an attempt to match
            /// the name hierarchy passed in the 'tag' parameter.
            /// If the whole hierarchy is resolved, the InnerText
            /// value at that point is returned. Note that this
            /// may itself be a subhierarchy of the entire
            /// document. The function returns a boolean indicator
            /// of the search's success. The search is performed
            /// by the recursive Search method.
            /// </summary>

            private static readonly char[] C_POINT = {'.'};

            private bool XmlFind(XmlElement root, string tag, out string result)
            {
                int nth = 0;
                if (root == null || tag == null || tag == String.Empty)
                { 
                    result = String.Empty;
                    return false;
                }
                return XmlSearch(root,tag.Split(C_POINT),0, ref nth, out result);
            }

            /// <summary>
            /// XmlSearch is initially called by XmlFind, and then
            /// recursively called by itself until the document
            /// supplied to XmlFind is either exhausted or the name hierarchy
            /// is matched. 
            ///
            /// If the hierarchy is matched, the value is returned in
            /// result, and true returned as the function's
            /// value. Otherwise the result is set to the empty string and
            /// false is returned.
            /// </summary>

            private bool XmlSearch(XmlElement e, string[] tags, int index, ref int nth, out string result)
            {
                if (index == tags.Length || e.Name != tags[index])
                {
                    result = String.Empty;
                    return false;
                }
                    
                if (tags.Length-index == 1)
                {
                    if (nth == 0)
                    {
                        result = e.InnerText;
                        return true;
                    }
                    else
                    {
                        nth--;
                        result = String.Empty;
                        return false;
                    }
                }

                if (e.HasChildNodes)
                {
                    XmlNodeList children = e.ChildNodes;
                    foreach (XmlNode node in children)
                    {
                       switch (node.NodeType)
                       {
                            case XmlNodeType.Element :
                                if (XmlSearch((XmlElement)node, tags, index+1, ref nth, out result))
                                    return true;
                                break;

                            default :
                                break;
                        }
                    }
                }

                result = String.Empty;
                return false;
            }

        }

        public class AssetRequest
        {
            public IClientAPI RequestUser;
            public UUID RequestAssetID;
            public AssetInfo AssetInf;
            public TextureImage ImageInfo;
            public UUID TransferRequestID;
            public long DataPointer = 0;
            public int NumPackets = 0;
            public int PacketCounter = 0;
            public bool IsTextureRequest;
            public byte AssetRequestSource = 2;
            public byte[] Params = null;
            //public bool AssetInCache;
            //public int TimeRequested;
            public int DiscardLevel = -1;
        }

        public class AssetInfo : AssetBase
        {
            public AssetInfo(AssetBase aBase)
            {
                Data = aBase.Data;
                FullID = aBase.FullID;
                Type = aBase.Type;
                Name = aBase.Name;
                Description = aBase.Description;
            }
        }

        public class TextureImage : AssetBase
        {
            public TextureImage(AssetBase aBase)
            {
                Data = aBase.Data;
                FullID = aBase.FullID;
                Type = aBase.Type;
                Name = aBase.Name;
                Description = aBase.Description;
            }
        }

        /// <summary>
        /// A list of requests for a particular asset.
        /// </summary>
        public class AssetRequestsList
        {
            public UUID assetId;
            public bool IsTexture;
            /// <summary>
            /// A list of requests for assets
            /// </summary>
            public List<NewAssetRequest> Requests = new List<NewAssetRequest>();

            /// <summary>
            /// Record the time that this request was first made.
            /// </summary>
            public DateTime TimeRequested;
        }

        /// <summary>
        /// Represent a request for an asset that has yet to be fulfilled.
        /// </summary>

        public class NewAssetRequest
        {
            public AssetRequestCallback Callback;
            public bool Retry;

            public NewAssetRequest(AssetRequestCallback callback, bool p_retry)
            {
                Callback = callback;
                Retry    = p_retry;
            }
        }

        /// <summary>
        /// This is the state carrier for locally initiated
        /// asynchronous asset requests.
        /// </summary>

        public class SyncRequest
        {

            private AssetBase m_asset = null;

            public SyncRequest()
            {
            }

            public AssetBase Wait(int timeout)
            {
                lock (this) 
                {
                    Monitor.Wait(this, timeout);
                    return m_asset;
                }
            }

            public void Post(UUID assetId, AssetBase asset)
            {
                lock (this)
                { 
                    m_asset = asset;
                    Monitor.Pulse(this);
                }
            }

        }

        #endregion
    }

    public class Cache : ICache
    {

        private static Dictionary<UUID,CacheEntry> cache = new Dictionary<UUID,CacheEntry>();

        // Return a count of the elements in the present cache

        public int Count
        {
            get { return cache.Count; }
        }

        // Get (or set) an entry using index notation

        public AssetBase this[UUID key]
        {
            get {lock (cache) return cache[key].Asset; }
            set {lock (cache) cache[key]= new CacheEntry(value, TimeSpan.FromHours(24)); }
        }

        // Conditionally retrieve the specified entry, return a boolean
        // assertion of success.

        public bool TryGet(UUID assetid, out AssetBase asset)
        {
            try
            {
                lock (cache) asset = cache[assetid].Asset;
                return true;
            }
            catch
            {
                asset = null;
                return false;
            }
        }

        // Test whether or not the named asset is in the cache

        public bool Contains(UUID assetid)
        {
           lock (cache) return cache.ContainsKey(assetid);
        }

        // Add (or Update) an entry in the cache.

        public void AddOrUpdate(UUID assetid, AssetBase asset, TimeSpan lifetime)
        {
            CacheEntry entry = new CacheEntry(asset, lifetime);
            try
            {
                lock (cache) cache.Add(assetid, entry);
                if (StatsManager.SimExtraStats != null)
                {
                    StatsManager.SimExtraStats.AddAsset(asset);
                }
            }
            catch (ArgumentException)
            {
                lock (cache)
                {
                    cache.Remove(assetid);
                    cache.Add(assetid, entry);
                }
            }
        }

        // Remove a specific entry from the cache

        public void Remove(UUID assetid)
        {
            try
            {
                lock (cache) cache.Remove(assetid);
            }
            catch
            {
            }
        }

        // Delete all entries from the cache

        public void Clear()
        {
            cache = new Dictionary<UUID, CacheEntry>();
        }

        // Local class definition for cache table entries

        internal class CacheEntry
        {
            internal AssetBase Asset    = null;
            internal TimeSpan  Lifetime;
 
            internal CacheEntry(AssetBase asset, TimeSpan life)
            {
                Asset = asset;
                Lifetime = life;
            }
        }

    }

    /// <summary>
    /// This defines the subset of the GT cache (plus extensions)
    /// needed by the asset cache.
    /// </summary>

    internal interface ICache
    {
        bool TryGet(UUID assetid, out AssetBase asset);
        bool Contains(UUID assetid);
        void AddOrUpdate(UUID assetid, AssetBase asset, TimeSpan life);
        void Remove(UUID assetid);
        int  Count { get; }
        void Clear();
        AssetBase this[UUID key] { get; set; }
    }

}
