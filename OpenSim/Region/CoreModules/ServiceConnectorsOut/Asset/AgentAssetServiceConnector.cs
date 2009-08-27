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

using log4net;
using Nini.Config;
using System;
using System.IO;
using System.Xml;
using System.Net;
using System.Collections.Specialized;
using System.Collections.Generic;
using System.Reflection;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Base;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

namespace OpenSim.Region.CoreModules.ServiceConnectorsOut.Asset
{
    public class AgentAssetServicesConnector :
            ISharedRegionModule, IAssetService
    {

        private static readonly ILog m_log =
                LogManager.GetLogger(
                MethodBase.GetCurrentMethod().DeclaringType);

        private IImprovedAssetCache m_Cache = null;

        private IAssetService m_AssetService;

        private bool m_Enabled = false;

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

        public Type ReplaceableInterface 
        {
            get { return null; }
        }

        public string Name
        {
            get { return "AgentAssetServicesConnector"; }
        }

        public void Initialise(IConfigSource source)
        {
            IConfig moduleConfig = source.Configs["Modules"];
            if (moduleConfig != null)
            {
                string name = moduleConfig.GetString("AssetServices", "");
                if (name == Name)
                {
                    m_log.Debug("[AGENT CONNECTOR]: Agent asset connector is being enabled");
                    IConfig assetConfig = source.Configs["AssetService"];
                    if (assetConfig == null)
                    {
                        m_log.Error("[AGENT CONNECTOR]: AssetService missing from OpenSim.ini");
                        return;
                    }

                    string serviceDll = assetConfig.GetString("LocalServiceModule",
                            String.Empty);

                    if (serviceDll == String.Empty)
                    {
                        m_log.Error("[AGENT CONNECTOR]: No LocalServiceModule named in section AssetService");
                        return;
                    }

                    Object[] args = new Object[] { source };
                    m_AssetService =
                            ServerUtils.LoadPlugin<IAssetService>(serviceDll,
                            args);

                    if (m_AssetService == null)
                    {
                        m_log.Error("[AGENT CONNECTOR]: Can't load asset service");
                        return;
                    }
                    m_Enabled = true;
                    LocalAgentAssetServerInit();
                    m_log.Info("[AGENT CONNECTOR]: Agent asset connector enabled");
                }
            }
        }

        public void PostInitialise()
        {
        }

        public void Close()
        {
        }

        public void AddRegion(Scene scene)
        {
            if (!m_Enabled)
                return;

            scene.RegisterModuleInterface<IAssetService>(this);
        }

        public void RemoveRegion(Scene scene)
        {
        }

        public void RegionLoaded(Scene scene)
        {
            if (!m_Enabled)
                return;

            if (m_Cache == null)
            {
                m_Cache = scene.RequestModuleInterface<IImprovedAssetCache>();

                if (!(m_Cache is ISharedRegionModule))
                    m_Cache = null;
            }

            m_log.InfoFormat("[AGENT CONNECTOR]: Enabled local assets for region {0}", scene.RegionInfo.RegionName);

            if (m_Cache != null)
            {
                m_log.InfoFormat("[AGENT CONNECTOR]: Enabled asset caching for region {0}", scene.RegionInfo.RegionName);
            }
            else
            {
                scene.UnregisterModuleInterface<IAssetService>(this);
                scene.RegisterModuleInterface<IAssetService>(m_AssetService);
            }
        }

        public AssetBase Get(string id)
        {
            AssetBase asset = null;
            if (m_Cache != null)
                asset = m_Cache.Get(id);

            if (asset == null)
            {
                asset = m_AssetService.Get(id);
                if (asset == null)
				{
                    m_log.DebugFormat("[AGENT CONNECTOR] 1");
					asset = lookElseWhere(id);
				}
                if ((m_Cache != null) && (asset != null))
                    m_Cache.Cache(asset);
            }
            return asset;
        }

        public AssetMetadata GetMetadata(string id)
        {
            AssetBase asset = null;
            if (m_Cache != null)
                asset = m_Cache.Get(id);

            if (asset != null)
                return asset.Metadata;

            asset = m_AssetService.Get(id);
			if (asset == null)
			{
				m_log.DebugFormat("[AGENT CONNECTOR] 2");
				asset = lookElseWhere(id);
			}
            if (asset != null) 
            {
                if (m_Cache != null)
                    m_Cache.Cache(asset);
                return asset.Metadata;
            }

            return null;
        }

        public byte[] GetData(string id)
        {
            AssetBase asset = m_Cache.Get(id);

            if (asset != null)
                return asset.Data;

            asset = m_AssetService.Get(id);
			if (asset == null)
			{
				m_log.DebugFormat("[AGENT CONNECTOR] 3");
				asset = lookElseWhere(id);
			}
            if (asset != null)
            {
                if (m_Cache != null)
                    m_Cache.Cache(asset);
                return asset.Data;
            }

            return null;
        }

        public bool Get(string id, Object sender, AssetRetrieved handler)
        {
            AssetBase asset = null;
            
            if (m_Cache != null)
                m_Cache.Get(id);

            if (asset != null)
            {
                handler.BeginInvoke(id, sender, asset, null, null);
                return true;
            }

            return m_AssetService.Get(id, sender, delegate (string assetID, Object s, AssetBase a)
            {
                if (a == null)
				{
					a = lookElseWhere(id);
				}

                if ((a != null) && (m_Cache != null))
                    m_Cache.Cache(a);
                
                handler.BeginInvoke(assetID, s, a, null, null);

            });
        }

        public string Store(AssetBase asset)
        {
            if (m_Cache != null)
                m_Cache.Cache(asset);
            
            if (asset.Temporary || asset.Local)
                return asset.ID;
            
            return m_AssetService.Store(asset);
        }

        public bool UpdateContent(string id, byte[] data)
        {
            AssetBase asset = null;
            if (m_Cache != null)
                m_Cache.Get(id);
            if (asset != null)
            {
                asset.Data = data;
                if (m_Cache != null)
                    m_Cache.Cache(asset);
            }

            return m_AssetService.UpdateContent(id, data);
        }

        public bool Delete(string id)
        {
            if (m_Cache != null)
                m_Cache.Expire(id);

            return m_AssetService.Delete(id);
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
            m_log.DebugFormat("[AGENT CONNECTOR] lookElsewhere {0}", assetId);

            AssetBase asset;
            RemoteAssetServer[] servers = RemoteServerList.ToArray();
            foreach (RemoteAssetServer server in servers)
            {
                if (server.GetAsset(assetId, out asset))
                {
                    m_log.DebugFormat("[AGENT CONNECTOR] lookElsewhere resolved {0}", assetId);
                    return asset;
                }
            }

            m_log.DebugFormat("[AGENT CONNECTOR] lookElsewhere abandoned {0}", assetId);
            return null;
        }

        /// <summary>
        /// This method hides the mechanics of maintaining the list
        /// of known servers.
        /// [AMW] A more sophisticated mechanism is needed.
        /// </summary>

        private void AddRemoteServer(string ipa, string port, string prefix)
        {
            m_log.DebugFormat("[AGENT CONNECTOR] AddRemoteServer {0} {1} {2}", ipa, port, prefix);
            RemoteServerList.Add(new RemoteAssetServer(ipa, port, prefix));
        }

        private bool FindRemoteServer(string ipa, string port)
        {
            m_log.DebugFormat("[AGENT CONNECTOR] FindRemoteServer {0} {1}", ipa, port);
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
                m_log.DebugFormat("[AGENT CONNECTOR] Remote Server CTOR  {0} {1} {2}", ipa, port, prefix);
                m_ipa    = ipa;
                m_port   = port;
                m_prefix = prefix;
            }

            private static readonly string GAP = "http://{0}:{1}{2}?asset={3}";

            public bool GetAsset(string assetid, out AssetBase asset)
            {

                m_log.DebugFormat("[AGENT CONNECTOR] GetAsset(W) {0}", assetid);

                string requrl = String.Format(GAP, m_ipa, m_port, m_prefix, assetid);
                XmlElement resp;

                if (webCall(requrl, out resp))
                {
                    string text;

                    if (XmlFind(resp, "body.asset", out text))
                    {
                        asset = new AssetBase();
                        asset.ID = assetid;
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

                m_log.DebugFormat("[AGENT CONNECTOR] Sending request <{0}>", requrl);

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
                    m_log.DebugFormat("[AGENT CONNECTOR] Web exception: {0}", w.Message);
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
                    m_log.DebugFormat("[AGENT CONNECTOR] <{0}>".PadLeft(index+14), e.Name);
                    XmlNodeList children = e.ChildNodes;
                    foreach (XmlNode node in children)
                       switch (node.NodeType)
                       {
                            case XmlNodeType.Element :
                                XmlScanl((XmlElement)node, index+1);
                                break;
                            case XmlNodeType.Text :
                                m_log.DebugFormat("[AGENT CONNECTOR] \"{0}\"".PadLeft(index+14), node.Value);
                                break;
                            default :
                                break;
                       }
                    m_log.DebugFormat("[AGENT CONNECTOR] </{0}>".PadLeft(index+15), e.Name);
                }
                else
                {
                    m_log.DebugFormat("[AGENT CONNECTOR] <{0}/>".PadLeft(index+15), e.Name);
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

        private void LocalAgentAssetServerInit()
        {
            
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
									m_log.InfoFormat("[AGENT CONNECTOR] Setting server listener port to {0}", s);
									m_port = Convert.ToUInt32(parts[1].Trim());
									break;
								case "prefix" :
									m_log.InfoFormat("[AGENT CONNECTOR] Setting asset server prefix to {0}", s);
									m_prefix = parts[1].Trim();
									break;
								case "register" :
									m_log.InfoFormat("[AGENT CONNECTOR] Setting registration prefix to {0}", s);
									m_register = parts[1].Trim();
									break;
								case "server" :
									m_log.InfoFormat("[AGENT CONNECTOR] Registering host {0}", s);
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
									m_log.DebugFormat("[AGENT CONNECTOR] Unrecognized command {0}:{1}", 
										parts[0], parts[1]);
									break;
							}
						}
						else
						{
							m_log.DebugFormat("[AGENT CONNECTOR] Ignoring line {0}", s);
						}
					}
					else
						m_log.DebugFormat("[AGENT CONNECTOR] Ignoring local host {0}", s);
				}
            }
            catch(Exception e)
            {
                m_log.DebugFormat("[AGENT CONNECTOR] Failed to read configuration file. {0}", e.Message);
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

            m_log.DebugFormat("[AGENT CONNECTOR] Register http://{0}:{1}{2}({3})",
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

            m_log.DebugFormat("[AGENT CONNECTOR] RemoteRequest http://{0}:{1}{2}({3})",
                req.Headers["remote_addr"], req.Headers["remote_port"], path, parms);

            // Remember all who try to talk to us ...

            if (!FindRemoteServer(req.Headers["remote_addr"], "9999"))
                AddRemoteServer(req.Headers["remote_addr"], "9999", m_prefix);
            
            if (DUMP)
            {
                foreach (string key in qstr.AllKeys)
                {
                   m_log.DebugFormat("[AGENT CONNECTOR] Query: {0:-10}:{1}", key, qstr.Get(key));
                }
                foreach (string key in hdrs.AllKeys)
                {
                   m_log.DebugFormat("[AGENT CONNECTOR] Header: {0:-10}:{1}", key, hdrs.Get(key));
                }
            }

            string assetid = getValue(qstr, "asset", String.Empty);
            bool   texture = Convert.ToBoolean(getValue(qstr, "texture", "false"));
            AssetBase asset = null;

            asset = m_Cache.Get(assetid);

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

    }
}
