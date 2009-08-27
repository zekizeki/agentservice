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
        /// in the .ini file.
        /// </summary>

        internal const int AC_VERSION = 1; // Serialization compatability version
        internal List<RemoteAssetServer> RemoteServerList = new List<RemoteAssetServer>();
        internal static bool DUMP   = false; // Generate dump of transfer data streams
        internal static uint m_port = 9999; // port that this server listens on
        internal static string m_prefix   = "/rrs/assetserver"; // path that accepts requests
        internal static string m_register = "/rrs/register"; // path that accepts server regs
        internal static string m_rafile = "interop.txt"; // default static config file
        internal static BaseHttpServer m_httpServer; // RAS server instance

        public ScatteredAssetService(IConfigSource config) : base(config)
        {

            m_log.DebugFormat("[SCATTERED ASSET SERVICE]: Initializing scattered asset service ");

            MainConsole.Instance.Commands.AddCommand("kfs", false,
                    "show digest",
                    "show digest <ID>",
                    "Show asset digest", HandleShowDigest);

            MainConsole.Instance.Commands.AddCommand("kfs", false,
                    "delete asset",
                    "delete asset <ID>",
                    "Delete asset from database", HandleDeleteAsset);

            IConfig assetConfig = config.Configs["AssetService"];

            if (assetConfig == null)
				return;

            if (m_AssetLoader != null)
            {
                string loaderArgs = assetConfig.GetString("AssetLoaderArgs",
                        String.Empty);

                m_log.InfoFormat("[SCATTERED ASSET SERVICE]: Loading default asset set from {0}", loaderArgs);
                m_AssetLoader.ForEachDefaultXmlAsset(loaderArgs,
                        delegate(AssetBase a)
                        {
                            Store(a);
                        });
                
                m_log.Info("[SCATTERED ASSET SERVICE]: Local asset service enabled");
            }

        	ScatteredAssetServerInit(assetConfig);

        }

        public AssetBase Get(string id)
        {
            AssetBase asset = null;
            UUID assetID;

            m_log.DebugFormat("[SCATTERED ASSET SERVICE]: Get(1) asset {0}", id);

            if (!UUID.TryParse(id, out assetID))
                return lookElseWhere(id);
            
            asset =  m_Database.GetAsset(assetID);

            if(asset == null)
                asset = lookElseWhere(id);

			return asset;

        }

        public AssetMetadata GetMetadata(string id)
        {
            AssetBase asset = null;
            UUID assetID;

            if (!UUID.TryParse(id, out assetID))
                asset = lookElseWhere(id);
			else
            	asset = m_Database.GetAsset(assetID);

            if(asset == null)
                asset = lookElseWhere(id);

            return asset.Metadata;
        }

        public byte[] GetData(string id)
        {
            AssetBase asset = null;
            UUID assetID;

            if (!UUID.TryParse(id, out assetID))
                asset = lookElseWhere(id);
			else
            	asset = m_Database.GetAsset(assetID);

            if(asset == null)
                asset = lookElseWhere(id);

            return asset.Data;
        }

        public bool Get(string id, Object sender, AssetRetrieved handler)
        {
			AssetBase asset = null;
            UUID assetID;

            m_log.DebugFormat("[SCATTERED ASSET SERVICE]: Get(2) asset {0}", id);

            if (!UUID.TryParse(id, out assetID))
                    asset = lookElseWhere(id);
            else
           		asset = m_Database.GetAsset(assetID);

            if (asset == null)
                asset = lookElseWhere(id);

            handler(id, sender, asset);

            return true;
        }

        public string Store(AssetBase asset)
        {
            m_Database.StoreAsset(asset);
            return asset.ID;
        }

        public bool UpdateContent(string id, byte[] data)
        {
            return false;
        }

        public bool Delete(string id)
        {
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
            m_log.DebugFormat("[SCATTERED ASSET SERVICE] lookElseWhere {0}", assetId);

            AssetBase asset;
            RemoteAssetServer[] servers = RemoteServerList.ToArray();
            foreach (RemoteAssetServer server in servers)
            {
                if (server.GetAsset(assetId, out asset))
                {
                    m_log.DebugFormat("[SCATTERED ASSET SERVICE] lookElseWhere resolved {0}", assetId);
                    return asset;
                }
            }

            m_log.DebugFormat("[SCATTERED ASSET SERVICE] lookElseWhere abandoned {0}", assetId);
            return null;
        }

        /// <summary>
        /// This method hides the mechanics of maintaining the list
        /// of known servers.
        /// [AMW] A more sophisticated mechanism is needed.
        /// </summary>

        private void AddRemoteServer(string ipa, string port, string prefix)
        {
            m_log.DebugFormat("[SCATTERED ASSET SERVICE] AddRemoteServer {0} {1} {2}", ipa, port, prefix);
            RemoteServerList.Add(new RemoteAssetServer(ipa, port, prefix));
        }

        private bool FindRemoteServer(string ipa, string port)
        {
            m_log.DebugFormat("[SCATTERED ASSET SERVICE] FindRemoteServer {0} {1}", ipa, port);
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

            private string m_ipa;
            private string m_port;
            private string m_prefix;

            public string ipa    { get { return m_ipa;    } }
            public string port   { get { return m_port;   } }
            public string prefix { get { return m_prefix; } }
           
            public RemoteAssetServer(string ipa, string port, string prefix)
            {
                m_log.DebugFormat("[SCATTERED ASSET SERVICE] Remote Server CTOR  {0} {1} {2}", ipa, port, prefix);
                m_ipa    = ipa;
                m_port   = port;
                m_prefix = prefix;
            }

            private static readonly string GAP = "http://{0}:{1}{2}?asset={3}";

            public bool GetAsset(string assetid, out AssetBase asset)
            {

                m_log.DebugFormat("[SCATTERED ASSET SERVICE] GetAsset(W) {0}", assetid);

                string requrl = String.Format(GAP, m_ipa, m_port, m_prefix, assetid);
                XmlElement resp;

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

                asset = null;
                return false;

            }

            private bool webCall(string requrl, out XmlElement resp)
            {

                // Otherwise prepare the request

                m_log.DebugFormat("[SCATTERED ASSET SERVICE] Sending request <{0}>", requrl);

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

                    if (DUMP) XmlScanl(doc.DocumentElement,0);

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
            m_port     = (uint) config.GetInt("RASPort", (int) m_port);
            m_prefix   = config.GetString("RASRequestPath", m_prefix);
            m_register = config.GetString("RASRegisterPath", m_register);
            m_rafile   = config.GetString("RASConfig", m_rafile);

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
									m_port = Convert.ToUInt32(parts[1].Trim());
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
									m_log.InfoFormat("[SCATTERED ASSET SERVICE] Registering host {0}", s);
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
											if (parts.Length > 1) prefix = "/"+parts[1].Trim();
										}
										AddRemoteServer(addr, port, prefix);
									}
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

            m_httpServer = new BaseHttpServer(m_port);
            m_httpServer.Start();

            m_httpServer.AddStreamHandler(new RestStreamHandler("GET", m_prefix,   RemoteRequest));
            m_httpServer.AddStreamHandler(new RestStreamHandler("GET", m_register, RegisterServer));
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

            string response = String.Empty;
            NameValueCollection qstr = req.QueryString;
            NameValueCollection hdrs = req.Headers;

            m_log.DebugFormat("[SCATTERED ASSET SERVICE] RemoteRequest http://{0}:{1}{2}({3})",
                req.Headers["remote_addr"], req.Headers["remote_port"], path, parms);

            // Remember all who try to talk to us ...

            if (!FindRemoteServer(req.Headers["remote_addr"], "9999"))
                AddRemoteServer(req.Headers["remote_addr"], "9999", m_prefix);
            
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

            AssetBase asset = m_Database.GetAsset(new UUID(assetid));

            if (asset == null)
            {
                rsp.StatusCode = 404;
                rsp.StatusDescription = "Asset not found";
            }
            else
            {
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
