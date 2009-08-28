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
 *     * Neither the name of the OpenSimulator Project nor the
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
using System.IO;
using System.Net;
using System.Xml;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Reflection;
using Nini.Config;
using log4net;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Framework.Console;
using OpenSim.Data;
using OpenSim.Services.Interfaces;
using OpenMetaverse;

namespace OpenSim.Services.AssetService
{
    public class ScatteredAssetService : AssetServiceBase, IAssetService
    {
        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        /// <summary>
        /// The following fields implement and control the operation of the
        /// distributed asset serving stuff. All of these can be configured
        /// in the .ini file. See ScatteredAssetServerInit for details.
        /// </summary>

        internal List<RemoteAssetServer> RemoteServerList = new List<RemoteAssetServer>();

        internal static string RRS_PORT = "9999";
        internal const  int    AC_VERSION = 1; // Serialization compatability version
        internal bool   DUMP       = false; // Generate dump of transfer data streams
        internal string m_port     = RRS_PORT; // port that this server listens on
        internal string m_prefix   = "/rrs/assetserver"; // path that accepts requests
        internal string m_register = "/rrs/register"; // path that accepts server regs
        internal string m_rafile   = "interop.txt"; // default static config file
        internal string m_server   = String.Empty; // grid server uri
		internal string m_uri      = String.Empty; // grid server uri

        internal BaseHttpServer m_httpServer; // RAS server instance

        public ScatteredAssetService(IConfigSource config) : base(config)
        {
            m_log.DebugFormat("[SCATTERED ASSET SERVICE] Initializing scattered asset service ");

            IConfig assetConfig = config.Configs["AssetService"];

            MainConsole.Instance.Commands.AddCommand("kfs", false,
                    "show digest",
                    "show digest <ID>",
                    "Show asset digest", HandleShowDigest);

            MainConsole.Instance.Commands.AddCommand("kfs", false,
                    "delete asset",
                    "delete asset <ID>",
                    "Delete asset from database", HandleDeleteAsset);

            if (assetConfig == null)
                return;

            // Load the scattered extensions. Must be done before loading starts.
            ScatteredAssetServerInit(assetConfig);

            // Load "standard" assets
            if (m_AssetLoader != null)
            {
                string loaderArgs = assetConfig.GetString("AssetLoaderArgs",
                        String.Empty);

                m_log.InfoFormat("[SCATTERED ASSET SERVICE] Loading default asset set from {0}", loaderArgs);
                m_AssetLoader.ForEachDefaultXmlAsset(loaderArgs,
                        delegate(AssetBase a)
                        {
                            Store(a);
                        });
                
                m_log.Info("[SCATTERED ASSET SERVICE] Local asset service enabled");
            }
        }

        // Synchronous GET request
 
        public AssetBase Get(string id)
        {
            AssetBase asset = null;
            UUID assetID;

            m_log.DebugFormat("[SCATTERED ASSET SERVICE] ENTRY Sync.Get <{0}>", id);

            if(m_Database != null)
            {
                if (UUID.TryParse(id, out assetID))
                {
					m_log.DebugFormat("[SCATTERED ASSET SERVICE] Sync.Get Searching Region database for <{0}>", id);
					asset = m_Database.GetAsset(assetID);
                }
            }

			if((asset == null) && (m_server != String.Empty))
			{
				m_log.DebugFormat("[SCATTERED ASSET SERVICE] Sync.Get Searching GRID database for <{0}>", id);
				asset = SynchronousRestObjectRequester.
						MakeRequest<int, AssetBase>("GET", m_uri, 0);
			}

			if (asset == null)
			{
				m_log.DebugFormat("[SCATTERED ASSET SERVICE] Sync.Get Searching scattered databases for <{0}>", id);
				asset = lookElseWhere(id);
			}

            if(asset == null)
            {
				m_log.DebugFormat("[SCATTERED ASSET SERVICE] Sync.Get Failed to resolve <{0}>", id);
            }

            m_log.DebugFormat("[SCATTERED ASSET SERVICE] EXIT Sync.Get <{0}>", id);

            return asset;

        }

        public AssetMetadata GetMetadata(string id)
        {
            AssetBase asset = null;

            m_log.DebugFormat("[SCATTERED ASSET SERVICE] ENTRY Request metadata for <{0}>", id);

            asset = Get(id);

            m_log.DebugFormat("[SCATTERED ASSET SERVICE] EXIT Request metadata for <{0}>", id);

            if(asset != null)
				return asset.Metadata;
            else
				return null;
        }

        public byte[] GetData(string id)
        {
            AssetBase asset = null;

            m_log.DebugFormat("[SCATTERED ASSET SERVICE] ENTRY Request content for <{0}>", id);

            asset = Get(id);

            m_log.DebugFormat("[SCATTERED ASSET SERVICE] EXIT Request content for <{0}>", id);

            if(asset != null)
				return asset.Data;
            else
				return null;
        }

        // Asynchronous GET request - call handler

        public bool Get(string id, Object sender, AssetRetrieved handler)
        {
            AssetBase asset = null;

            m_log.DebugFormat("[SCATTERED ASSET SERVICE] ENTRY ASync Request content for <{0}>", id);

            asset = Get(id);
            handler(id, sender, asset);

            m_log.DebugFormat("[SCATTERED ASSET SERVICE] EXIT ASync Request content for <{0}>", id);

            return true;
        }

        public string Store(AssetBase asset)
        {

            UUID   assetID;
            string   newID = String.Empty;

            m_log.DebugFormat("[SCATTERED ASSET SERVICE] ENTRY Store <{0}>", asset.ID);

            if(asset.Temporary)
            {
				m_log.DebugFormat("[SCATTERED ASSET SERVICE] EXIT <{0}> is temporary, not stored anywhere", asset.ID);
                return asset.ID;
            }

            // If the local data base exists, and the ID is
            // a valid UUID. Look to see if the asset 
            // already exists. If it does, then store it 
            // and return the newly assigned asset ID.

            if (m_Database != null)
            {
                if (UUID.TryParse(asset.ID, out assetID))
                {
                    if (m_Database.ExistsAsset(assetID))
                    {
                        asset.FullID = UUID.Random();
                        asset.ID     = asset.FullID.ToString();
                    }
					m_log.DebugFormat("[SCATTERED ASSET SERVICE] Storing {0} locally", asset.ID);
					m_Database.StoreAsset(asset);
                    newID = asset.ID;
                }
			}

            if(! asset.Local && m_server != String.Empty)
            {
				try
				{
					newID = SynchronousRestObjectRequester.
							MakeRequest<AssetBase, string>("POST", m_uri, asset);
					m_log.DebugFormat("[SCATTERED ASSET SERVICE] Stored {0} on GRID asset server", asset.ID);
				}
				catch (Exception e)
				{
					m_log.WarnFormat("[SCATTERED ASSET SERVICE] Failed to forward {0} to server. Reason: {1}", 
									asset.ID, e.Message);
                    newID = asset.ID;
				}
			}

            m_log.DebugFormat("[SCATTERED ASSET SERVICE] EXIT Store <{0}/{1}>", asset.ID, newID);

            return newID;

        }

        public bool UpdateContent(string id, byte[] data)
        {

            AssetBase asset   = null;

            m_log.DebugFormat("[SCATTERED ASSET SERVICE] ENTRY Conditionally update content of {0}", id);

            asset = Get(id);

            if (asset == null)
            {
                m_log.DebugFormat("[SCATTERED ASSET SERVICE] {0} does not already exist", id);

                AssetMetadata metadata = GetMetadata(id);

                if (metadata == null)
                {
					m_log.DebugFormat("[SCATTERED ASSET SERVICE] EXIT Failed to create metadata for {0}", id);
                    return false;
                }

                asset = new AssetBase();
                asset.Metadata = metadata;

            }

            asset.Data = data;

            id = Store(asset);

            m_log.DebugFormat("[SCATTERED ASSET SERVICE] EXIT Conditionally update content of <{0}>", asset.ID);

            return (id != String.Empty);

        }

        public bool Delete(string id)
        {
  
            AssetBase asset;

            m_log.DebugFormat("[SCATTERED ASSET SERVICE] ENTRY {0} delete requested", id);

            asset = Get(id);

            if(asset != null)
            {
				// Just from the local database, if it is there
				if (asset != null && (asset.Local || asset.Temporary))
				{
					m_log.WarnFormat("[SCATTERED ASSET SERVICE] <{0}> local deletion not supported");
				}
                else if(m_server != String.Empty)
                {
					if (SynchronousRestObjectRequester.
							MakeRequest<int, bool>("DELETE", m_uri, 0))
					{
						m_log.DebugFormat("[SCATTERED ASSET SERVICE] EXIT <{0}> server deletion complete");
						return true;
					}
                }
            }
            m_log.DebugFormat("[SCATTERED ASSET SERVICE] EXIT {0} delete failed", id);
            return false;
        }

        void HandleShowDigest(string module, string[] args)
        {
            if (args.Length < 3)
            {
                MainConsole.Instance.Output("Syntax: show digest <ID>");
                return;
            }

            AssetBase asset = Get(args[2]);

            if (asset == null || asset.Data.Length == 0)
            {   
                MainConsole.Instance.Output("Asset not found");
                return;
            }

            int i;

            MainConsole.Instance.Output(String.Format("Name: {0}", asset.Name));
            MainConsole.Instance.Output(String.Format("Description: {0}", asset.Description));
            MainConsole.Instance.Output(String.Format("Type: {0}", asset.Type));
            MainConsole.Instance.Output(String.Format("Content-type: {0}", asset.Metadata.ContentType));

            for (i = 0 ; i < 5 ; i++)
            {
                int off = i * 16;
                if (asset.Data.Length <= off)
                    break;
                int len = 16;
                if (asset.Data.Length < off + len)
                    len = asset.Data.Length - off;

                byte[] line = new byte[len];
                Array.Copy(asset.Data, off, line, 0, len);

                string text = BitConverter.ToString(line);
                MainConsole.Instance.Output(String.Format("{0:x4}: {1}", off, text));
            }
        }

        void HandleDeleteAsset(string module, string[] args)
        {
            if (args.Length < 3)
            {
                MainConsole.Instance.Output("Syntax: delete asset <ID>");
                return;
            }

            AssetBase asset = Get(args[2]);

            if (asset == null || asset.Data.Length == 0)
            {   
                MainConsole.Instance.Output("Asset not found");
                return;
            }

            Delete(args[2]);

            //MainConsole.Instance.Output("Asset deleted");
            // TODO: Implement this

            MainConsole.Instance.Output("Asset deletion not supported by database");
        }

        #region remote asset server

        /// <summary>
        /// This is the common remote asset search method. It is called
        /// for both client and region requests. It tries all of the 
        /// remote servers it knows about and declares victory on the 
        /// first positive response. If it cannot be found, then a null
        /// reference is returned.
        /// </summary>

        private AssetBase lookElseWhere(string assetId)
        {
            m_log.DebugFormat("[SCATTERED ASSET SERVICE] Searching for <{0}>", assetId);
            AssetBase asset;
            RemoteAssetServer[] servers = RemoteServerList.ToArray();
            foreach (RemoteAssetServer server in servers)
            {
                if (server.GetAsset(assetId, out asset))
                {
                    m_log.DebugFormat("[SCATTERED ASSET SERVICE] Asset resolved {0} by server {1}", 
                                assetId, server.ID);
                    return asset;
                }
            }
            m_log.DebugFormat("[SCATTERED ASSET SERVICE] {0} not found", assetId);
            return null;
        }

        /// <summary>
        /// This method hides the mechanics of maintaining the list
        /// of known servers.
        /// [AMW] A more sophisticated mechanism is needed.
        /// </summary>

        private void AddRemoteServer(string ipa, string port, string prefix)
        {
            RemoteServerList.Add(new RemoteAssetServer(this, ipa, port, prefix));
        }

        private bool FindRemoteServer(string ipa, string port)
        {
            foreach (RemoteAssetServer server in RemoteServerList)
                if (server.ipa == ipa && server.port == port)
                    return true;
            return false;
        }

        /// <summary>
        /// This class represents a remote server. All communication
        /// is via this class.
        /// </summary>
 
        public class RemoteAssetServer
        {

            private ScatteredAssetService g_service = null;
            private bool   g_rrs;
            private string g_ipa;
            private string g_port;
            private string g_prefix;

            public string ipa    { get { return g_ipa;    } }
            public string port   { get { return g_port;   } }
            public string prefix { get { return g_prefix; } }
           
            public string ID { get { return g_ipa+":"+g_port+":"+g_prefix; }}

            public RemoteAssetServer(ScatteredAssetService service, string ipa, string port, string prefix)
            {
                m_log.DebugFormat("[SCATTERED ASSET SERVICE] {0} {1} {2} added to known server table", 
                            ipa, port, prefix);
                g_service = service;
                g_ipa     = ipa;
                g_port    = port;
                g_prefix  = prefix;
                g_rrs     = (port == RRS_PORT);
            }

            private static readonly string GAP1 = "http://{0}:{1}{2}?asset={3}";
            private static readonly string GAP2 = "http://{0}:{1}{2}{3}";

            public bool GetAsset(string assetid, out AssetBase asset)
            {

                if (g_rrs)
                {
                    string requrl = String.Format(GAP1, g_ipa, g_port, g_prefix, assetid);
                    XmlElement resp;

                    m_log.DebugFormat("[SCATTERED ASSET SERVICE] RSS request sent to  <{0}>", requrl);

                    if (webCall(requrl, out resp))
                    {
                        string text;
                        if (XmlFind(resp, "body.asset", out text))
                        {
                            int version = 0;
                            if (XmlFind(resp, "body.asset.version", out text))
                                version = Convert.ToInt32(text);
                            // For now we will assume backward compatability
                            if(version >= AC_VERSION)
                            {
                                asset = new AssetBase();
                                if (XmlFind(resp, "body.asset.data", out text))
                                    asset.Data = Convert.FromBase64String(text);
                                if (XmlFind(resp, "body.asset.id", out text))
                                    asset.Metadata.ID = text;
                                if (XmlFind(resp, "body.asset.fullid", out text))
                                    asset.Metadata.FullID = new UUID(text);
                                if (XmlFind(resp, "body.asset.name", out text))
                                    asset.Metadata.Name = text;
                                if (XmlFind(resp, "body.asset.desc", out text))
                                    asset.Metadata.Description = text;
                                if (XmlFind(resp, "body.asset.type", out text))
                                    asset.Metadata.Type = SByte.Parse(text);
                                if (XmlFind(resp, "body.asset.ctype", out text))
                                    asset.Metadata.ContentType = text;
                                asset.Metadata.Temporary = true;
                                asset.Metadata.Local     = true;
                                return true;
                            }
                        }
                    }
                }
                else
                {
                    string requrl = String.Format(GAP2, g_ipa, g_port, "/assets/", assetid);
                    m_log.DebugFormat("[SCATTERED ASSET SERVICE] OSS request sent to <{0}>", requrl);
                    asset = SynchronousRestObjectRequester.
                        MakeRequest<int, AssetBase>("GET", requrl, 0);
                    if ( asset != null)
                        return true;
                }

                m_log.DebugFormat("[SCATTERED ASSET SERVICE] Could not resolve <{0}>", assetid);

                asset = null;
                return false;

            }

            private bool webCall(string requrl, out XmlElement resp)
            {

                // Otherwise prepare the request

                resp = null;

                HttpWebRequest  req = null;
                HttpWebResponse rsp = null;

                try
                {
                    req = (HttpWebRequest)WebRequest.Create(requrl);            
                    req.ContentLength = 0;
                    // We are sending just parameters, no content
                    // Send request and retrieve the response
                    rsp = (HttpWebResponse) req.GetResponse();

                    XmlTextReader rdr = new XmlTextReader(rsp.GetResponseStream());
                    XmlDocument   doc = new XmlDocument();
                    doc.Load(rdr);
                    rdr.Close();

                    // If we're debugging server responses, dump the whole
                    // load now

                    if (g_service.DUMP) XmlScanl(doc.DocumentElement,0);

                    resp = doc.DocumentElement;
                    return true;

                }
                catch (WebException w)
                {
                    m_log.DebugFormat("[SCATTERED ASSET SERVICE] Web exception: {0}", w.Message);
                }

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
                    m_log.DebugFormat("[SCATTERED ASSET SERVICE] <{0}>".PadLeft(index+14), e.Name);
                    XmlNodeList children = e.ChildNodes;
                    foreach (XmlNode node in children)
                       switch (node.NodeType)
                       {
                            case XmlNodeType.Element :
                                XmlScanl((XmlElement)node, index+1);
                                break;
                            case XmlNodeType.Text :
                                m_log.DebugFormat("[SCATTERED ASSET SERVICE] \"{0}\"".PadLeft(index+14), node.Value);
                                break;
                            default :
                                break;
                       }
                    m_log.DebugFormat("[SCATTERED ASSET SERVICE] </{0}>".PadLeft(index+15), e.Name);
                }
                else
                {
                    m_log.DebugFormat("[SCATTERED ASSET SERVICE] <{0}/>".PadLeft(index+15), e.Name);
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
        #endregion

        #region Agent Asset Server

        private void ScatteredAssetServerInit(IConfig config)
        {
            
            // Agent parameters

            DUMP       = config.GetBoolean("RASXML", DUMP);
            m_port     = config.GetString("RASPort", RRS_PORT);
            m_prefix   = config.GetString("RASRequestPath", m_prefix);
            m_register = config.GetString("RASRegisterPath", m_register);
            m_rafile   = config.GetString("RASConfig", m_rafile);
            m_server   = config.GetString("AssetServerURI", String.Empty);

            if(m_server != String.Empty)
            {
				m_uri      = String.Format("{0}/assets/", m_server);
				AddServer(m_server);
            }

            if(File.Exists(m_rafile))
            {

                // Load the starting set of known asset servers
               
                string local = Util.GetLocalHost().ToString();

                try
                {
                    string[] slist = File.ReadAllLines(m_rafile);

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
                                        m_log.InfoFormat("[SCATTERED ASSET SERVICE] Setting server listener port to {0}", s);
                                        m_port = parts[1].Trim();
                                        break;
                                    case "prefix" :
                                        m_log.InfoFormat("[SCATTERED ASSET SERVICE] Setting asset server prefix to {0}", s);
                                        m_prefix = parts[1].Trim();
                                        break;
                                    case "register" :
                                        m_log.InfoFormat("[SCATTERED ASSET SERVICE] Setting registration prefix to {0}", s);
                                        m_register = parts[1].Trim();
                                        break;
                                    case "server" :
                                        AddServer(parts[1]);
                                        break;
                                    default :
                                        m_log.DebugFormat("[SCATTERED ASSET SERVICE] Unrecognized command {0}:{1}", 
                                            parts[0], parts[1]);
                                        break;
                                }
                            }
                            else
                            {
                                m_log.DebugFormat("[SCATTERED ASSET SERVICE] Ignoring line {0}", s);
                            }
                        }
                        else
                            m_log.DebugFormat("[SCATTERED ASSET SERVICE] Ignoring local host {0}", s);
                    }
                }
                catch(Exception e)
                {
                    m_log.DebugFormat("[SCATTERED ASSET SERVICE] Failed to read configuration file. {0}", e.Message);
                }
            }

            m_httpServer = new BaseHttpServer((uint) Convert.ToInt32(m_port));
            m_httpServer.Start();

            m_httpServer.AddStreamHandler(new RestStreamHandler("GET", m_prefix,   RemoteRequest));
            m_httpServer.AddStreamHandler(new RestStreamHandler("GET", m_register, RegisterServer));

        }

        private void AddServer(string addr)
        {
            string   port = String.Empty;
            string   path = String.Empty;
            int      ci;

            m_log.InfoFormat("[SCATTERED ASSET SERVICE] Registering host {0}", addr);
         
            if (addr == String.Empty)
                return;

            if (addr.StartsWith("http://")) addr = addr.Substring(7);

            ci = addr.IndexOf('/');

            if (ci != -1)
            {
                path = addr.Substring(ci, addr.Length-ci).Trim();
                addr = addr.Substring(0,ci).Trim();
            }
     
            ci = addr.IndexOf(':');

            if (ci != -1)
            {
                port = addr.Substring(ci+1, addr.Length-ci-1).Trim();
                addr = addr.Substring(0,ci).Trim();
            }
            
            addr = addr.Trim();

            // If our special port is used, it MUST be our special path
            if (port == m_port)
                path = m_prefix;

            if (port == String.Empty)
                port = "80";

            if (addr != String.Empty)
                AddRemoteServer(addr, port, path);

        }

        #endregion

        #region Remote web interface

        // This is WAY too promiscuous, but will suffice for initial testing

        private string RegisterServer(string body, string path, string parms, OSHttpRequest req, OSHttpResponse rsp)
        {
            NameValueCollection qstr = req.QueryString;

            m_log.DebugFormat("[SCATTERED ASSET SERVICE] Register http://{0}:{1}{2}({3})",
                req.Headers["remote_addr"], req.Headers["remote_port"], path, parms);

            string ipa  = getValue(qstr, "ip",   String.Empty);
            string port = getValue(qstr, "port", String.Empty);

            AddRemoteServer(ipa, port, m_prefix);

            return String.Empty;

        }

        private string RemoteRequest(string body, string path, string parms, OSHttpRequest req, OSHttpResponse rsp)
        {

            AssetBase asset = null;
            string response = String.Empty;

            NameValueCollection qstr = req.QueryString;
            NameValueCollection hdrs = req.Headers;

            if (DUMP)
            m_log.DebugFormat("[SCATTERED ASSET SERVICE] Remote request for received: http://{0}:{1}{2}({3})",
                req.Headers["remote_addr"], req.Headers["remote_port"], path, parms);

            // Remember all who try to talk to us ...

            if (!FindRemoteServer(req.Headers["remote_addr"], RRS_PORT))
                AddRemoteServer(req.Headers["remote_addr"], RRS_PORT, m_prefix);
            
            if (DUMP)
            {
                foreach (string key in qstr.AllKeys)
                {
                   m_log.DebugFormat("[SCATTERED ASSET SERVICE] Query: {0:-10}:{1}", key, qstr.Get(key));
                }
                foreach (string key in hdrs.AllKeys)
                {
                   m_log.DebugFormat("[SCATTERED ASSET SERVICE] Header: {0:-10}:{1}", key, hdrs.Get(key));
                }
            }

            string assetid = getValue(qstr, "asset", String.Empty);

            m_log.DebugFormat("[SCATTERED ASSET SERVICE] Remote request for asset base {0}", assetid);

            if(m_Database != null)
            {
                asset = m_Database.GetAsset(new UUID(assetid));
            }

            if (asset == null)
            {
                m_log.DebugFormat("[SCATTERED ASSET SERVICE] {0} was not found locally", assetid);
                rsp.StatusCode = 404;
                rsp.StatusDescription = "Asset not found";
            }
            else
            {
                m_log.DebugFormat("[SCATTERED ASSET SERVICE] {0} was resolved successfully", assetid);
                // Explicit serialization protects us from server
                // differences
                response = String.Format("<body><asset>{0}</asset></body>", 
                    String.Format("<version>{0}</version>", AC_VERSION) +
                    String.Format("<data>{0}</data>", Convert.ToBase64String(asset.Data)) +
                    String.Format("<id>{0}</id>", asset.Metadata.ID) +
                    String.Format("<fullid>{0}</fullid>", asset.Metadata.FullID.ToString()) +
                    String.Format("<name>{0}</name>", asset.Metadata.Name) +
                    String.Format("<desc>{0}</desc>", asset.Metadata.Description) +
                    String.Format("<type>{0}</type>", asset.Metadata.Type) +
                    String.Format("<ctype>{0}</ctype>", asset.Metadata.ContentType) +
                    String.Format("<local>{0}</local>", asset.Metadata.Local) +
                    String.Format("<temp>{0}</temp>", asset.Metadata.Temporary)
                );
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

    }
}
