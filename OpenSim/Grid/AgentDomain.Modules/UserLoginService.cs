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
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security; //DWL
using System.Security.Cryptography.X509Certificates; //DWL
using System.Reflection;
using System.Text.RegularExpressions;
using log4net;
using Nwc.XmlRpc;
using OpenMetaverse;
// Structured Data
using OpenMetaverse.StructuredData;
using OSD = OpenMetaverse.StructuredData.OSD;
using OSDMap = OpenMetaverse.StructuredData.OSDMap;
using OSDArray = OpenMetaverse.StructuredData.OSDArray;

using OpenSim.Data;
using OpenSim.Framework;
using OpenSim.Framework.Communications;
using OpenSim.Framework.Communications.Services;
using OpenSim.Framework.Communications.Cache;
using OpenSim.Framework.Capabilities;
using OpenSim.Framework.Servers;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Grid.Communications.OGS1;

namespace OpenSim.Grid.AgentDomain.Modules
{
    public delegate void UserLoggedInAtLocation(UUID agentID, UUID sessionID, UUID RegionID,
                                                ulong regionhandle, float positionX, float positionY, float positionZ,
                                                string firstname, string lastname);

    /// <summary>
    /// Login service used in grid mode.
    /// </summary>     
    public class UserLoginService : LoginService

    {
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        public event UserLoggedInAtLocation OnUserLoggedInAtLocation;

        private UserLoggedInAtLocation handlerUserLoggedInAtLocation;

        public UserConfig m_config;
        private readonly IRegionProfileRouter m_regionProfileService;

        protected BaseHttpServer m_httpServer;

	// AD hacks 

	public int event_queue_counter = 0;
        
        protected UserConfig Cfg;
        private string ipHostString;
        private string ipHostPort;
        private UUID m_lastCreatedUser;
        private NameUUIDEntry m_uuid_table = new NameUUIDEntry();
        private ActiveAgentState m_state_table = new ActiveAgentState();
	    private UserDataBaseService m_userDataBaseService; // DWL
        private IInterServiceInventoryServices m_interInventoryService;
        private GridInventoryService m_inventoryService;

	// X.509 bypass stuff
        private static bool customXertificateValidation(object sender, X509Certificate cert, X509Chain chain, SslPolicyErrors error)
         {
            return true;
        }

	// End top AD hacks


        public UserLoginService(UserDataBaseService dataBaseServer,
            UserManagerBase userManager, IInterServiceInventoryServices inventoryService,
            LibraryRootFolder libraryRootFolder,
            UserConfig config, string welcomeMess, IRegionProfileRouter regionProfileService)
            : base(userManager, libraryRootFolder, welcomeMess)
        {
            m_config = config;
            m_defaultHomeX = m_config.DefaultX;
            m_defaultHomeY = m_config.DefaultY;
            m_userDataBaseService = dataBaseServer;
            m_interInventoryService = inventoryService;
            m_regionProfileService = regionProfileService;
            ipHostPort = (m_config.HttpPort).ToString();
	    setupHostNames();
            m_log.InfoFormat("[AGENT DOMAIN]: listening on port {0}",ipHostPort);
	    m_log.InfoFormat("[AGENT DOMAIN]: Host name is: {0} ",ipHostString);
	    
	    InventoryConfig invCfg = new InventoryConfig("INVENTORY", (Path.Combine(Util.configDir(), "InventoryServer_Config.xml")));

            m_inventoryService = new GridInventoryService(invCfg.UserServerURL);
            m_inventoryService.DoLookup = invCfg.SessionLookUp;
            m_inventoryService.AddPlugin(invCfg.DatabaseProvider, invCfg.DatabaseConnect);
        }


	// Grab host names for caps
        
        public void setupHostNames()
        {
            string hostName = Dns.GetHostName();
            Console.WriteLine("Local hostname: {0}", hostName);
            IPHostEntry myself = Dns.GetHostByName(hostName);
            foreach (IPAddress address in myself.AddressList)
            {
                ipHostString = address.ToString();
                Console.WriteLine("IP Address: {0}",ipHostString);
                
            }
        }

	// -----------------------------------------
	// AD code goes in here for now
        // Once we get it working, more clean up
	// ------------------------------------------
 // ---------------------------------------------------------------- 
        // 
        // Helper to do the derez on logout work 
        // 
        // ------------------------------------------------------------------ 
        
        
        public void doDerez(UUID agentToDerez, string firstName, string lastName) 
        { 
            m_log.InfoFormat("[Agent Domain]: doing derez logout Case with agent uuid {0} name {1} {2}",agentToDerez.ToString(),firstName, lastName); 
            Agent_state_entry state = m_state_table.lookupAgentStateByUUID(agentToDerez); 
            if (state.agentState != AgentADState.rezzed) 
             { 
                m_log.WarnFormat("[Agent Domain]: Attempt to Derez agent {0} with name of {1} {2} when we think it is not rezzed. Resetting agent state.",agentToDerez.ToString(),firstName,lastName); 
                m_state_table.updateAgentState(agentToDerez,AgentADState.logged_out); 
                return; 
            } 
            string derezCap = state.derezCap; 
            m_log.InfoFormat("[Agent Domain]: Derez cap to invoke is {0}",derezCap); 
            WebRequest derezRequest = null; 
            try 
            { 
                derezRequest = WebRequest.Create(derezCap); 
            } 
            catch (Exception ex) 
            { 
                m_log.InfoFormat("[Agent Domain]: Bad URL derez_avatar {0} region Cap was {1}", ex.Message,derezCap); 
            } 
            derezRequest.Method = "POST"; 
            derezRequest.ContentType = "application/xml+llsd"; 
            // Build out the request 
            OSDMap DRMap = new OSDMap(); // Map of the parms 
            DRMap["first_name"] = OSD.FromString(firstName); 
            DRMap["last_name"] = OSD.FromString(lastName); 
            string rezCap = "http://"+ipHostString+":"+ipHostPort+"/cap/rez_avatar/derez "; 
            DRMap["rez_avatar/rez"] = OSD.FromString(rezCap); 
            string DRMapString = DRMap.ToString(); 
            
            m_log.InfoFormat("[Agent Domain] DRMap string {0}", DRMapString); 
            OSD LLSDofRAMap = DRMap; 
            byte[] buffer1 = OSDParser.SerializeLLSDXmlBytes(LLSDofRAMap); 
            
            // Try to post the rez avatar reqest 
            Stream os1 = null; 
            try 
            { // send the Post 
                derezRequest.ContentLength = buffer1.Length; //Count bytes to send 
                os1 =derezRequest.GetRequestStream(); 
                os1.Write(buffer1, 0, buffer1.Length); //Send it 
                os1.Close(); 
                m_log.InfoFormat("[Agent Domain]: derez Avatar request to remote sim {0}", derezCap); 
            } 
            catch (Exception ex) 
            { 
                m_log.InfoFormat("[Agent Domain]: Bad send on derez_avatar {0}", ex.Message); 
            } 
            m_log.Info("[Agent Domain]: waiting for a reply after derez send"); 
            // We should probably look at the reply, but for now, just force logged out 
            m_state_table.updateAgentState(agentToDerez,AgentADState.logged_out); 
        } 
        
        
        // ------------------------------------------------------- 
        // 
        // Helper for the place call, when the ave is rezzed in a sim 
        // 
        // In this case, we are either logging out (null new location) 
        // or teleporting 
        // 
        // ------------------------------------------------------------ 
        
        public OSD handleRezzedCase(string path, OSD request, string endpoint, UUID agentUUID, string capUsed, string storedFirstName, string storedLastName) 
        { 
            m_log.InfoFormat("[Agent Domain]: handleRezzed Case with agent {0} {1}",storedFirstName, storedLastName); 
            Boolean logoutRequest = false; 
            OSDMap responseMap = new OSDMap(); 
            OSDMap requestMap = (OSDMap)request; 
            string regionCapAddress = requestMap["public_region_seed_capability"].AsString(); 
            // do proper derez soon 
            if ("" == regionCapAddress ) 
             { 
                m_log.InfoFormat("[Agent Domain]: place avatar with null seedcap. Assume logout."); 
                doDerez(agentUUID,storedFirstName,storedLastName); 
                logoutRequest = true; 
                responseMap["connect"] = OSD.FromBoolean(false); 
                return responseMap; 
            } 
            // We need to do a "request rez" on the new sim, then do a derez on the old sim, pointing at the new sim 
            // So... 
            // First we fetch out what we need to do the request rez 
            // NOTE: Out variable here 
            string derezCap; 
            UUID secureSessionID; 
            Int32 circuitCode; 
            Agent_state_entry state = m_state_table.lookupAgentStateByUUID(agentUUID); 
            if (state.agentState == AgentADState.unknown) 
             { 
                m_log.WarnFormat("Agent Domain]: Major error, failed to find stored state for agent UUID {0}",agentUUID.ToString()); 
                responseMap["connect"] = OSD.FromBoolean(false); 
                return responseMap; 
            } 
            else 
             { 
                secureSessionID = state.secureSessionID; 
                circuitCode = state.circuitCode; 
                derezCap = state.derezCap; 
            } 
            // This does the rez_avatar/request request 
            
            string regionCapString = regionCapAddress+"/agent/rez_avatar/request"; 
            m_log.InfoFormat("adRezAvatarPlace: public region seed cap {0}",regionCapString); 
            WebRequest rezRequest = null; 
            
            try 
            { 
                rezRequest = WebRequest.Create(regionCapString); 
            } 
            catch (Exception ex) 
            { 
                m_log.InfoFormat("[Agent Domain]: Bad URL rez_avatar {0} region Cap was {1}", ex.Message,regionCapString); 
                responseMap["connect"] = OSD.FromBoolean(false); 
                return responseMap; 
            } 
            rezRequest.Method = "POST"; 
            rezRequest.ContentType = "application/xml+llsd"; 
            // Build out the request 
            OSDMap RAMap = new OSDMap(); // Map of the parms 
            RAMap["agent_id"] = OSD.FromUUID(agentUUID); 
            RAMap["circuit_code"] = OSD.FromInteger(circuitCode); 
            RAMap["secure_session_id"] = OSD.FromUUID(secureSessionID); 
            RAMap["first_name"] = OSD.FromString(storedFirstName); 
            RAMap["last_name"] = OSD.FromString(storedLastName);
            string capBit = m_uuid_table.getCapForUUID(agentUUID);
	    string inventoryResolveCapString = "http://"+ipHostString+":"+ipHostPort+"/agent/inventory_resolver/"+capBit;
	    RAMap["inventory_resolver_cap"] = OSD.FromString(inventoryResolveCapString);
	    
	    // [rob] add caps for the inventory reflector to use
	    string inventoryReflectCapString = "http://"+ipHostString+":"+ipHostPort+"/agent/inventory_create/"+capBit;
	    RAMap["inventory_create_cap"] = OSD.FromString(inventoryReflectCapString);
	    inventoryReflectCapString = "http://"+ipHostString+":"+ipHostPort+"/agent/inventory_update/"+capBit;
	 	RAMap["inventory_update_cap"] = OSD.FromString(inventoryReflectCapString);   
        inventoryReflectCapString = "http://"+ipHostString+":"+ipHostPort+"/agent/inventory_move/"+capBit;
	 	RAMap["inventory_move_cap"] = OSD.FromString(inventoryReflectCapString);
	 	inventoryReflectCapString = "http://"+ipHostString+":"+ipHostPort+"/agent/inventory_delete/"+capBit;
	 	RAMap["inventory_delete_cap"] = OSD.FromString(inventoryReflectCapString);
	 	inventoryReflectCapString = "http://"+ipHostString+":"+ipHostPort+"/agent/inventoryfolder_create/"+capBit;
	 	RAMap["inventoryfolder_create_cap"] = OSD.FromString(inventoryReflectCapString);
	 	inventoryReflectCapString = "http://"+ipHostString+":"+ipHostPort+"/agent/inventoryfolder_update/"+capBit;
	 	RAMap["inventoryfolder_update_cap"] = OSD.FromString(inventoryReflectCapString);
	 	inventoryReflectCapString = "http://"+ipHostString+":"+ipHostPort+"/agent/inventoryfolder_move/"+capBit;
	 	RAMap["inventoryfolder_move_cap"] = OSD.FromString(inventoryReflectCapString);
	 	inventoryReflectCapString = "http://"+ipHostString+":"+ipHostPort+"/agent/inventoryfolder_delete/"+capBit;
	 	RAMap["inventoryfolder_delete_cap"] = OSD.FromString(inventoryReflectCapString);  
	 	inventoryReflectCapString = "http://"+ipHostString+":"+ipHostPort+"/agent/wearables/update/"+capBit;
	 	RAMap["wearables_update_cap"] = OSD.FromString(inventoryReflectCapString);
        
	 	
            RAMap["avatar_wearables"] = GetAgentWearables(agentUUID);
            
            
	    
	    
	   
            
            string RAMapString = RAMap.ToString(); 
            
            m_log.InfoFormat("[ZHA Agent Domain] RAMap string {0}", RAMapString); 
            OSD LLSDofRAMap = RAMap; 
            byte[] buffer = OSDParser.SerializeLLSDXmlBytes(LLSDofRAMap); 
            
            
            
            
            // Try to post the rez avatar reqest 
            Stream os = null; 
            try 
            { // send the Post 
                rezRequest.ContentLength = buffer.Length; //Count bytes to send 
                os =rezRequest.GetRequestStream(); 
                os.Write(buffer, 0, buffer.Length); //Send it 
                os.Close(); 
                m_log.InfoFormat("[Agent Domain]: Place Avatar Posted Rez Avatar request to remote sim {0}", regionCapAddress); 
            } 
            catch (Exception ex) 
            { 
                m_log.InfoFormat("[Agent Domain]: Bad send on rez_avatar {0}", ex.Message); 
                responseMap["connect"] = OSD.FromBoolean(false); 
                return responseMap; 
            } 
            m_log.Info("[Agent Domain]: waiting for a reply after rez avatar/request send"); 
            // Read the reply 
            OSD rezResponse = null; 
            OSDMap rezResponseMap = new OSDMap(); 
            string rez_avatar_reply = null; 
            { // "get the response nesting block" 
                try 
                { 
                    WebResponse webResponse = rezRequest.GetResponse(); 
                    if (webResponse == null) 
                     { 
                        m_log.Info("[Agent Domain:] Null reply on rez_avatar post"); 
                    } 
                    StreamReader sr = new StreamReader(webResponse.GetResponseStream()); 
                    rez_avatar_reply = sr.ReadToEnd().Trim(); 
                    m_log.InfoFormat("[Agent Domain]: rez_avatar reply was {0} ", rez_avatar_reply); 
                } 
                catch (WebException ex) 
                { 
                    m_log.InfoFormat("[Agent Domain]: exception on read after send of rez avatar {0}", ex.Message); 
                    responseMap["connect"] = OSD.FromBoolean(false); 
                    return responseMap; 
                } 
                try 
                { 
                    rezResponse = OSDParser.DeserializeLLSDXml(rez_avatar_reply); 
                    rezResponseMap = (OSDMap)rezResponse; 
                } 
                catch (Exception ex) 
                { 
                    m_log.InfoFormat("[Agent Domain]: exception on parse of rez reply {0}", ex.Message); 
                    responseMap["connect"] = OSD.FromBoolean(false); 
                    return responseMap; 
                } 
            } // end of "get the response nesting block" 
            // Now we have cool stuff for the next step 
            string rez_avatarCapString = rezResponseMap["rez_avatar/rez"].AsString(); 
            string derez_avatarCapString = rezResponseMap["rez_avatar/derez"].AsString(); 
            string region_seedCapString = rezResponseMap["region_seed_capability"].AsString(); 
            m_log.InfoFormat("[Agent Domain]: rez_avatar cap on reply is {0}",rez_avatarCapString); 
            m_log.InfoFormat("[Agent Domain]: derez_avatar cap on reply is {0}",derez_avatarCapString); 
            m_log.InfoFormat("[Agent Domain]: derez_avatar region seed cap on reply is {0}",region_seedCapString); 
            
            // make sure we store this derez cap, otherwise we'll always send derez requests to our start region.
            state.derezCap = derez_avatarCapString;
            
            // -------------------------------------------------------------------- 
            // Now we do the derez avatar on the old sim, pointing it at the new one 
            // -------------------------------------------------------------------- 
            // deprecated string derezCap = getDerezCapFromUUID(agentUUID); 
            m_log.InfoFormat("[Agent Domain]: saved derez cap is {0} ",derezCap); 
            WebRequest derezRequest = null; 
            try 
            { 
                derezRequest = WebRequest.Create(derezCap); 
            } 
            catch (Exception ex) 
            { 
                m_log.InfoFormat("[Agent Domain]: Bad URL derez_avatar {0} Cap was {1}", ex.Message,derezCap); 
                responseMap["connect"] = OSD.FromBoolean(false); 
                return responseMap; 
            } 
            derezRequest.Method = "POST"; 
            derezRequest.ContentType = "application/xml+llsd"; 
            // Build out the request 
            OSDMap derezMap = new OSDMap(); // Map of the parms 
            derezMap["rez_avatar/rez"] = OSD.FromString(rez_avatarCapString); 
            string derezMapString = derezMap.ToString(); 
            m_log.InfoFormat("[Agent Domain] derezMap string {0}",derezMapString); 
            OSD LLSDOfDerezMap = derezMap; 
            byte[] buffer1 = OSDParser.SerializeLLSDXmlBytes(LLSDOfDerezMap); 
            // Try to post the rez avatar reqest 
            Stream os1 = null; 
            try 
            { // send the Post 
                derezRequest.ContentLength = buffer1.Length; //Count bytes to send 
                os1 =derezRequest.GetRequestStream(); 
                os1.Write(buffer1, 0, buffer1.Length); //Send it 
                os1.Close(); 
                m_log.InfoFormat("[Agent Domain]: Place Avatar Posted deRez Avatar request to remote sim {0}", derezCap); 
            } 
            catch (Exception ex) 
            { 
                m_log.InfoFormat("[Agent Domain]: Bad send on derez_avatar {0}", ex.Message); 
                responseMap["connect"] = OSD.FromBoolean(false); 
                return responseMap; 
            } 
            m_log.Info("[Agent Domain]: waiting for a reply after derez avatar send"); 
            { // "get the response nesting block" 
                string derez_avatar_reply; 
                OSD derezResponse = null; 
                OSDMap derezResponseMap = null; 
                try 
                { 
                    WebResponse webResponse = derezRequest.GetResponse(); 
                    if (webResponse == null) 
                     { 
                        m_log.Info("[Agent Domain:] Null reply on rez_avatar post"); 
                    } 
                    StreamReader sr = new StreamReader(webResponse.GetResponseStream()); 
                    derez_avatar_reply = sr.ReadToEnd().Trim(); 
                    m_log.InfoFormat("[Agent Domain]: rez_avatar reply was {0} ", derez_avatar_reply); 
                } 
                catch (WebException ex) 
                { 
                    m_log.InfoFormat("[Agent Domain]: exception on read after send of rez avatar {0}", ex.Message); 
                    responseMap["connect"] = OSD.FromBoolean(false); 
                    return responseMap; 
                } 
                // parse things up 
                try 
                { 
                    derezResponse = OSDParser.DeserializeLLSDXml(derez_avatar_reply); 
                    derezResponseMap = (OSDMap)derezResponse; 
                } 
                catch (Exception ex) 
                { 
                    m_log.InfoFormat("[Agent Domain]: exception on parse of derez reply {0}", ex.Message); 
                    responseMap["connect"] = OSD.FromBoolean(false); 
                    return responseMap; 
                } 
                responseMap = derezResponseMap; 
            } // end of nesting block 
            
            return responseMap; 
        } 
        
        public OSDArray GetAgentWearables(UUID agentUUID)
        {
            // (Rob) pass the user appearance data here for now, maybe expose via a cap later ?
            m_log.Info("[Agent Domain] GetAgentWearables getting appearance info");
            AvatarAppearance appearance = m_userDataBaseService.GetUserAppearance(agentUUID);
            
            if(appearance == null)
            {
                m_log.Error("[Agent Domain] GetAgentWearables we have a null appearance");
                
                // if they dont have an appearance they wont have an inventory, create it now
                m_inventoryService.CreateNewUserInventory(agentUUID);
                //m_interInventoryService.CreateNewUserInventory(agentUUID);
                
                // ok lets create the default appearance
                appearance = new AvatarAppearance(agentUUID, GetDefaultWearables(), GetDefaultVisualParams());
                
                // save the appearance
                m_userDataBaseService.UpdateUserAppearance(agentUUID,appearance);
                
            }
            AvatarWearable[] wearables = appearance.Wearables;
            
            
            
            OSDArray wearablesOSD = new OSDArray();
            
            for(int i = 0; i < wearables.Length;i++)
            {
                m_log.InfoFormat("[Agent Domain] adding wearable {0}", wearables[i].AssetID); 
                OSDMap osdWearable = new OSDMap();
                osdWearable["AssetID"] = OSD.FromUUID(wearables[i].AssetID);
                osdWearable["ItemID"] = OSD.FromUUID(wearables[i].ItemID);
                wearablesOSD.Add(osdWearable);
            }
            
            return wearablesOSD;    
        }
        
        private static AvatarWearable[] GetDefaultWearables()
        {
                AvatarWearable[] defaultWearables = new AvatarWearable[13]; //should be 13 of these
                for (int i = 0; i < 13; i++)
                {
                    defaultWearables[i] = new AvatarWearable();
                }
                
                // Body
                defaultWearables[0].ItemID  = new UUID("66c41e39-38f9-f75a-024e-585989bfaba9");
                defaultWearables[0].AssetID = new UUID("66c41e39-38f9-f75a-024e-585989bfab73");                

                // Skin
                defaultWearables[1].ItemID  = new UUID("77c41e39-38f9-f75a-024e-585989bfabc9");
                defaultWearables[1].AssetID = new UUID("77c41e39-38f9-f75a-024e-585989bbabbb");

                // Shirt
                defaultWearables[4].ItemID  = new UUID("77c41e39-38f9-f75a-0000-585989bf0000");
                defaultWearables[4].AssetID = new UUID("00000000-38f9-1111-024e-222222111110");

                // Pants
                defaultWearables[5].ItemID  = new UUID("77c41e39-38f9-f75a-0000-5859892f1111");
                defaultWearables[5].AssetID = new UUID("00000000-38f9-1111-024e-222222111120");
                
                // rob added these to have > 4 items
                defaultWearables[2].ItemID  = new UUID("00000000-0000-0000-0000-000000000000");
                defaultWearables[2].AssetID = new UUID("00000000-0000-0000-0000-000000000000");
                 defaultWearables[3].ItemID  = new UUID("00000000-0000-0000-0000-000000000000");
                defaultWearables[3].AssetID = new UUID("00000000-0000-0000-0000-000000000000");
                 defaultWearables[6].ItemID  = new UUID("00000000-0000-0000-0000-000000000000");
                defaultWearables[6].AssetID = new UUID("00000000-0000-0000-0000-000000000000");
                 defaultWearables[7].ItemID  = new UUID("00000000-0000-0000-0000-000000000000");
                defaultWearables[7].AssetID = new UUID("00000000-0000-0000-0000-000000000000");
                 defaultWearables[8].ItemID  = new UUID("00000000-0000-0000-0000-000000000000");
                defaultWearables[8].AssetID = new UUID("00000000-0000-0000-0000-000000000000");
                 defaultWearables[9].ItemID  = new UUID("00000000-0000-0000-0000-000000000000");
                defaultWearables[9].AssetID = new UUID("00000000-0000-0000-0000-000000000000");
                 defaultWearables[10].ItemID  = new UUID("00000000-0000-0000-0000-000000000000");
                defaultWearables[10].AssetID = new UUID("00000000-0000-0000-0000-000000000000");
                 defaultWearables[11].ItemID  = new UUID("00000000-0000-0000-0000-000000000000");
                defaultWearables[11].AssetID = new UUID("00000000-0000-0000-0000-000000000000");
                 defaultWearables[12].ItemID  = new UUID("00000000-0000-0000-0000-000000000000");
                defaultWearables[12].AssetID = new UUID("00000000-0000-0000-0000-000000000000");
                
                
                
                return defaultWearables;
        }
        
         private static byte[] GetDefaultVisualParams()
        {
            byte[] visualParams;
            visualParams = new byte[218];
            for (int i = 0; i < 218; i++)
            {
                visualParams[i] = 100;
            }
            return visualParams;
        }
        
        // -------------------------------------- 
        // 
        // simple rez avatar place handler 
        // 
        // We check to see if the agent is already rezzed. If so, we are donig a de-rez, with a destination of the new place. If that 
        // place is null, it is a logout. If it it not rezzed, then we do a simple initial place. 
        // 
        // --------------------------------------- 
        
        
        public OSD adRezAvatarPlace(string path, OSD request, string endpoint) 
        { 
            m_log.InfoFormat("AdRezAvatarPlace called with {0}",request); 
            OSDMap responseMap = new OSDMap(); 
            string[] pathSegments = path.Split('/'); 
            int i; 
            for (i = 0;i<pathSegments.Length;i++) 
            { 
                m_log.InfoFormat("[Agent Domain]: rez avatar place segment {0} is {1}",i,pathSegments[i]); 
            } 
            if (pathSegments.Length <4) 
             { 
                m_log.WarnFormat("[Agent Domain]: badly formed rez_avatar_place request {0}",path); 
                responseMap["connect"] = OSD.FromBoolean(false); 
                return responseMap; 
            } 
            string capSegment = pathSegments[4]; 
            // Look up to see if we know this cap 
            UUID agentUUID = m_uuid_table.getAgentUUIDforCap(capSegment); 
            if (UUID.Zero == agentUUID) 
             if (pathSegments.Length < 2) 
             { 
                m_log.WarnFormat("[Agent Domain]: Rez Avatar Place invalid seed cap fetch path {0} from endpoint {1}",path,endpoint); 
                responseMap["connect"] = OSD.FromBoolean(true); 
                return responseMap; 
            } 
            // string capSufix = pathSegments[2]; 
            Agent_state_entry agentInfo = m_state_table.lookupAgentStateByUUID(agentUUID); 
            string storedFirstName = agentInfo.firstName; 
            string storedLastName = agentInfo.lastName; 
            AgentADState agentState = agentInfo.agentState; 
            
            // We check the known state of the agent. It should be one of 
            // logged_in (in which case this is the initial place) 
            // rezzed (in which case this is a teleport) 
            if (agentState == AgentADState.rezzed) 
             { 
                return handleRezzedCase( path, request, endpoint, agentUUID, capSegment, storedFirstName, storedLastName); 
            } 
            
            OSDMap requestMap = (OSDMap)request; 
            string regionCapAddress = requestMap["public_region_seed_capability"].AsString(); 
            if ("" == regionCapAddress ) 
             { 
                m_log.InfoFormat("[Agent Domain]: place avatar with null seedcap. Assume logout."); 
                
                responseMap["connect"] = OSD.FromBoolean(false); 
                return responseMap; 
            } 
            
            // This does the rez_avatar/request request 
            
            string regionCapString = regionCapAddress+"/agent/rez_avatar/request"; 
            m_log.InfoFormat("adRezAvatarPlace: public region seed cap {0}",regionCapString); 
            WebRequest rezRequest = null; 
            
            try 
            { 
                rezRequest = WebRequest.Create(regionCapString); 
            } 
            catch (Exception ex) 
            { 
                m_log.InfoFormat("[Agent Domain]: Bad URL rez_avatar {0} region Cap was {1}", ex.Message,regionCapString); 
                responseMap["connect"] = OSD.FromBoolean(false); 
                return responseMap; 
            } 
            rezRequest.Method = "POST"; 
            rezRequest.ContentType = "application/xml+llsd"; 
            // Build out the request 
            OSDMap RAMap = new OSDMap(); // Map of the parms 
            RAMap["agent_id"] = OSD.FromUUID(agentUUID); 
            Int32 circuitCodeInt = (Int32)Util.RandomClass.Next(); 
            RAMap["circuit_code"] = OSD.FromInteger( circuitCodeInt); 
            UUID secureSessionUUID = UUID.Random(); 
            RAMap["secure_session_id"] = OSD.FromUUID(secureSessionUUID); 
            RAMap["first_name"] = OSD.FromString(storedFirstName); 
            RAMap["last_name"] = OSD.FromString(storedLastName); 
            string capBit = m_uuid_table.getCapForUUID(agentUUID);
	    string inventoryResolveCapString = "http://"+ipHostString+":"+ipHostPort+"/agent/inventory_resolver/"+capBit;
	    RAMap["inventory_resolver_cap"] = OSD.FromString(inventoryResolveCapString);
	    // [rob] add a cap for the inventory reflector to use
        string inventoryReflectCapString = "http://"+ipHostString+":"+ipHostPort+"/agent/inventory_create/"+capBit;
        RAMap["inventory_create_cap"] = OSD.FromString(inventoryReflectCapString);
        inventoryReflectCapString = "http://"+ipHostString+":"+ipHostPort+"/agent/inventory_update/"+capBit;
	 	RAMap["inventory_update_cap"] = OSD.FromString(inventoryReflectCapString);
	 	inventoryReflectCapString = "http://"+ipHostString+":"+ipHostPort+"/agent/inventory_move/"+capBit;
	 	RAMap["inventory_move_cap"] = OSD.FromString(inventoryReflectCapString);
	 	inventoryReflectCapString = "http://"+ipHostString+":"+ipHostPort+"/agent/inventory_delete/"+capBit;
	 	RAMap["inventory_delete_cap"] = OSD.FromString(inventoryReflectCapString);
	 	inventoryReflectCapString = "http://"+ipHostString+":"+ipHostPort+"/agent/inventoryfolder_create/"+capBit;
	 	RAMap["inventoryfolder_create_cap"] = OSD.FromString(inventoryReflectCapString);
	 	inventoryReflectCapString = "http://"+ipHostString+":"+ipHostPort+"/agent/inventoryfolder_update/"+capBit;
	 	RAMap["inventoryfolder_update_cap"] = OSD.FromString(inventoryReflectCapString);
	 	inventoryReflectCapString = "http://"+ipHostString+":"+ipHostPort+"/agent/inventoryfolder_move/"+capBit;
	 	RAMap["inventoryfolder_move_cap"] = OSD.FromString(inventoryReflectCapString);
	 	inventoryReflectCapString = "http://"+ipHostString+":"+ipHostPort+"/agent/inventoryfolder_delete/"+capBit;
	 	RAMap["inventoryfolder_delete_cap"] = OSD.FromString(inventoryReflectCapString);
	 	inventoryReflectCapString = "http://"+ipHostString+":"+ipHostPort+"/agent/wearables/update/"+capBit;
	 	RAMap["wearables_update_cap"] = OSD.FromString(inventoryReflectCapString);

	    
            RAMap["avatar_wearables"] = GetAgentWearables(agentUUID);
            
            string RAMapString = RAMap.ToString(); 
            m_log.InfoFormat("[Agent Domain] RAMap string {0}", RAMapString); 
            OSD LLSDofRAMap = RAMap; 
            byte[] buffer = OSDParser.SerializeLLSDXmlBytes(LLSDofRAMap); 
            
            // Try to post the rez avatar reqest 
            Stream os = null; 
            try 
            { // send the Post 
                rezRequest.ContentLength = buffer.Length; //Count bytes to send 
                os =rezRequest.GetRequestStream(); 
                os.Write(buffer, 0, buffer.Length); //Send it 
                os.Close(); 
                m_log.InfoFormat("[Agent Domain]: Place Avatar Posted Rez Avatar request to remote sim {0}", regionCapAddress); 
            } 
            catch (Exception ex) 
            { 
                m_log.InfoFormat("[Agent Domain]: Bad send on rez_avatar {0}", ex.Message); 
                responseMap["connect"] = OSD.FromBoolean(false); 
                return responseMap; 
            } 
            m_log.Info("[Agent Domain]: waiting for a reply after rez avatar/request send"); 
            // Read the reply 
            OSD rezResponse = null; 
            OSDMap rezResponseMap = new OSDMap(); 
            string rez_avatar_reply = null; 
            { // "get the response nesting block" 
                try 
                { 
                    WebResponse webResponse = rezRequest.GetResponse(); 
                    if (webResponse == null) 
                     { 
                        m_log.Info("[Agent Domain:] Null reply on rez_avatar post"); 
                    } 
                    StreamReader sr = new StreamReader(webResponse.GetResponseStream()); 
                    rez_avatar_reply = sr.ReadToEnd().Trim(); 
                    m_log.InfoFormat("[Agent Domain]: rez_avatar reply was {0} ", rez_avatar_reply); 
                } 
                catch (WebException ex) 
                { 
                    m_log.InfoFormat("[Agent Domain]: exception on read after send of rez avatar {0}", ex.Message); 
                    responseMap["connect"] = OSD.FromBoolean(false); 
                    return responseMap; 
                } 
                try 
                { 
                    rezResponse = OSDParser.DeserializeLLSDXml(rez_avatar_reply); 
                    rezResponseMap = (OSDMap)rezResponse; 
                } 
                catch (Exception ex) 
                { 
                    m_log.InfoFormat("[Agent Domain]: exception on parse of rez reply {0}", ex.Message); 
                    responseMap["connect"] = OSD.FromBoolean(false); 
                    return responseMap; 
                } 
            } // end of "get the response nesting block" 
            // Now we invoke the rez_avatar/rez cap on the sim 
            string rez_avatarCapString = rezResponseMap["rez_avatar/rez"].AsString(); 
            string derez_avatarCapString = rezResponseMap["rez_avatar/derez"].AsString(); 
            string region_seedCapString = rezResponseMap["region_seed_capability"].AsString(); 
            m_log.InfoFormat("[Agent Domain]: rez_avatar cap on reply is {0}",rez_avatarCapString); 
            m_log.InfoFormat("[Agent Domain]: derez_avatar cap on reply is {0}",derez_avatarCapString); 
            m_log.InfoFormat("[Agent Domain]: derez_avatar region seed cap on reply is {0}",region_seedCapString); 
            
            WebRequest rezavRequest = null; 
            try 
            { 
                rezavRequest = WebRequest.Create(rez_avatarCapString); 
            } 
            catch ( Exception ex) 
            { 
                m_log.InfoFormat("[Agent Domain]: exception on rez cav cap {0} cap was {1}", ex.Message,rez_avatarCapString); 
                m_log.WarnFormat("[Agent Domain]: Resetting agent state.",agentUUID.ToString(),storedFirstName,storedLastName); 
                m_state_table.updateAgentState(agentUUID,AgentADState.logged_out);
                responseMap["connect"] = OSD.FromBoolean(false); 
                return responseMap; 
            } 
            rezavRequest.Method = "POST"; 
            rezavRequest.ContentType = "application/xml+llsd"; 
            OSDMap rez_avatarRezMap = new OSDMap(); 
            rez_avatarRezMap["secure_session_id"] = OSD.FromUUID(secureSessionUUID); 
            UUID sessionUUID = UUID.Random(); 
            rez_avatarRezMap["session_id"] = OSD.FromUUID(sessionUUID); 
            rez_avatarRezMap["circuit_code"] = OSD.FromInteger(circuitCodeInt); 
            string rez_avatarRezMapString = rez_avatarRezMap.ToString(); 
            m_log.InfoFormat("[Agent Domain] rez_avatarRezMap string {0}", rez_avatarRezMapString); 
            OSD LLSDofRAZMap = rez_avatarRezMap; 
            byte[] rezBuffer = OSDParser.SerializeLLSDXmlBytes(LLSDofRAZMap); 
            
            // Try to post the rez avatar reqest 
            os = null; 
            try 
            { // send the Post 
                rezavRequest.ContentLength = rezBuffer.Length; //Count bytes to send 
                os =rezavRequest.GetRequestStream(); 
                os.Write(rezBuffer, 0, rezBuffer.Length); //Send it 
                os.Close(); 
                m_log.InfoFormat("[Agent Domain]: Place Avatar Posted Rez Avatar rez to remote sim {0}", regionCapAddress); 
            } 
            catch (WebException ex) 
            { 
                m_log.InfoFormat("[Agent Domain]: Bad send on rez_avatar rez {0}", ex.Message); 
                responseMap["connect"] = OSD.FromBoolean(false); 
                return responseMap; 
            } 
            m_log.Info("[Agent Domain]: waiting for a reply after rez avatar rez send"); 
            // Read the reply 
            OSD rezRezResponse = null; 
            OSDMap rezRezResponseMap = new OSDMap(); 
            string rezrez_avatar_reply = null; 
            { // "get the response nesting block" 
                try 
                { 
                    WebResponse rezrezWebResponse = rezavRequest.GetResponse(); 
                    if (rezrezWebResponse == null) 
                     { 
                        m_log.Info("[Agent Domain:] Null reply on rez_avatar rez post"); 
                    } 
                    StreamReader sr = new StreamReader(rezrezWebResponse.GetResponseStream()); 
                    rezrez_avatar_reply = sr.ReadToEnd().Trim(); 
                    m_log.InfoFormat("[Agent Domain]: rez_avatar rez reply was {0} ", rezrez_avatar_reply); 
                } 
                catch (WebException ex) 
                { 
                    m_log.InfoFormat("[Agent Domain]: exception on read after send of rez avatar rez {0}", ex.Message); 
                    responseMap["connect"] = OSD.FromBoolean(false); 
                    return responseMap; 
                } 
                
                try 
                { 
                    rezRezResponse = OSDParser.DeserializeLLSDXml(rezrez_avatar_reply); 
                    rezRezResponseMap = (OSDMap)rezRezResponse; 
                } 
                catch (Exception ex) 
                { 
                    m_log.InfoFormat("[Agent Domain]: exception on parse of rez rez reply {0}", ex.Message); 
                    responseMap["connect"] = OSD.FromBoolean(false); 
                    return responseMap; 
                } 
                 
            } // end of "get the response nesting block" 
            rezRezResponseMap["region_seed_capability"] = OSD.FromString(region_seedCapString); 
            rezRezResponseMap["agent_id"] = OSD.FromUUID(agentUUID); 
            
            // Rob we can get the inventory skeleton here instead of from the region, then its not linked to the regions inventory store
            //   m_log.Info("[Agent Domain]: getting inventory skeleton");

            m_log.Info("[Agent Domain]: getting root folder");
            rezRezResponseMap["inventory_root"] = getRootFolder(agentUUID);
            m_log.InfoFormat("[Agent Domain]: sending response map {0}",rezRezResponseMap);
            // Just before we hand off the response to the client, update our state, so we know the client was 
            // sent to the region, and what de-rez cap to call 
            m_state_table.updateAgentStateAndCapAndStuff (agentUUID, AgentADState.rezzed , derez_avatarCapString,secureSessionUUID,circuitCodeInt ); 
            return rezRezResponseMap; 
        } 


	// -----------------------------------------
	//
	// Get Inventory skeleton for a given agent
	//
	// -----------------------------------------
        
        public OSD getInventorySkeleton(UUID agentUUID)
        {
            List<InventoryFolderBase> folderList = null;
            //folderList = m_interInventoryService.GetInventorySkeleton(agentUUID);
            folderList = m_inventoryService.GetInventorySkeleton(agentUUID);
            
            OSDArray skelArray = new OSDArray();
            
            foreach (InventoryFolderBase InvFolder in folderList)
            {
                
                OSDMap osdRow = new OSDMap();
                osdRow["name"] = OSD.FromString(InvFolder.Name);
                osdRow["version"] = OSD.FromInteger((int)InvFolder.Version);   
                osdRow["parent_id"] = OSD.FromUUID(InvFolder.ParentID); 
                osdRow["type_default"] = OSD.FromInteger((int)InvFolder.Type); 
                osdRow["folder_id"] = OSD.FromUUID(InvFolder.ID);       
                skelArray.Add(osdRow);
            }
            
            return skelArray;
        }
        
        public OSD getRootFolder(UUID agentUUID)
        {    
	InventoryFolderBase rootFolder = new InventoryFolderBase(); // DWL
        return OSD.FromUUID(rootFolder.ID);   
        }
        
        public OSD adAgentInfoStub(string path, OSD request, string endpoint)
        {
            m_log.Info("AdAgentInfoStub called");
            OSDMap responseMap = new OSDMap();	
            return responseMap;
        }
        
        public OSD adEventQueueStub(string path, OSD request, string endpoint)
        {
	    //m_log.InfoFormat("adEventQueueStub entry {0}",event_queue_counter++);
            OSDMap responseMap = new OSDMap();	
	    OSDArray eventsArray = new OSDArray();
	    OSDMap helloMap = new OSDMap();
	    OSDMap echoMap = new OSDMap();
            helloMap["ZhaMessages"] = OSD.FromString("Hello Madam Viewer");
	    echoMap["message"] = OSD.FromString("echo");
	    eventsArray.Add(helloMap);
	    eventsArray.Add(echoMap);
	    responseMap["events"] = eventsArray;
	    responseMap["id"] = OSD.FromInteger(event_queue_counter);
            return responseMap;
        }
        
        
        
        public string webFetchInventoryDescendentsRequest(string request, string path, string param,OSHttpRequest httpRequest, OSHttpResponse httpResponse)
        {
            m_log.Debug("[Main.cs]: 	InventoryDescendentsRequest is "+request);
            
            // nasty temporary hack here, the linden client falsely identifies the uuid 00000000-0000-0000-0000-000000000000 as a string which breaks us
            // correctly mark it as a uuid
            request = request.Replace("<string>00000000-0000-0000-0000-000000000000</string>", "<uuid>00000000-0000-0000-0000-000000000000</uuid>");
            
            // another hack <integer>1</integer> results in a System.ArgumentException: Object type System.Int32 cannot be converted to target type: System.Boolean
            request = request.Replace("<key>fetch_folders</key><integer>0</integer>", "<key>fetch_folders</key><boolean>0</boolean>");
            request = request.Replace("<key>fetch_folders</key><integer>1</integer>", "<key>fetch_folders</key><boolean>1</boolean>");
            Hashtable hash = new Hashtable();
            try
            {
                hash = (Hashtable)LLSD.LLSDDeserialize(Utils.StringToBytes(request));
            }
            catch (LLSD.LLSDParseException pe)
            {
                m_log.Error("[AGENT INVENTORY]: Fetch error: " + pe.Message);
                m_log.Error("Request: " + request.ToString());
            }
            
            ArrayList foldersrequested = (ArrayList)hash["folders"];
            
            string response = "";
            for (int i = 0; i < foldersrequested.Count; i++)
            {
                string inventoryitemstr = "";
                Hashtable inventoryhash = (Hashtable)foldersrequested[i];
                
                LLSDFetchInventoryDescendents llsdRequest = new LLSDFetchInventoryDescendents();
                
                try{
                    LLSDHelpers.DeserialiseOSDMap(inventoryhash, llsdRequest);
                }
                catch(Exception e)
                {
                    m_log.Debug("[CAPS]: caught exception doing OSD deserialize" + e);
                }
                LLSDInventoryDescendents reply = FetchInventoryReply(llsdRequest);
                
                inventoryitemstr = LLSDHelpers.SerialiseLLSDReply(reply);
                inventoryitemstr = inventoryitemstr.Replace("<llsd><map><key>folders</key><array>", "");
                inventoryitemstr = inventoryitemstr.Replace("</array></map></llsd>", "");
                
                response += inventoryitemstr;
            }
            
            
            if (response.Length == 0)
             {
                // Ter-guess: If requests fail a lot, the client seems to stop requesting descendants.
                // Therefore, I'm concluding that the client only has so many threads available to do requests
                // and when a thread stalls..   is stays stalled.
                // Therefore we need to return something valid
                response = "<llsd><map><key>folders</key><array /></map></llsd>";
            }
            else
             {
                response = "<llsd><map><key>folders</key><array>" + response + "</array></map></llsd>";
            }
            
            m_log.DebugFormat("[CAPS]: Replying to CAPS fetch inventory request with following xml");
            m_log.Debug("[CAPS] "+response);
            
            return response;
        }
        
        /// <summary>
        /// Construct an LLSD reply packet to a CAPS inventory request
        /// </summary>
        /// <param name="invFetch"></param>
        /// <returns></returns>

        private LLSDInventoryDescendents FetchInventoryReply(LLSDFetchInventoryDescendents invFetch)
        {
            LLSDInventoryDescendents reply = new LLSDInventoryDescendents();
            LLSDInventoryFolderContents contents = new LLSDInventoryFolderContents();
            //contents.agent_id = m_agentID;
            contents.owner_id =  invFetch.owner_id;
            contents.folder_id = invFetch.folder_id;
            
            // The version number being sent back was originally 1.
            // Unfortunately, on 1.19.1.4, this means that we see a problem where on subsequent logins
            // without clearing client cache, objects in the root folder disappear until the cache is cleared,
            // at which point they reappear.
            //
            // Seeing the version to something other than 0 may be the right thing to do, but there is
            // a greater subtlety of the second life protocol that needs to be understood first.
            contents.version = 0;
            
            contents.descendents = 0;
            reply.folders.Array.Add(contents);
            List<InventoryItemBase> itemList = null;
            itemList = m_inventoryService.RequestFolderItems(invFetch.folder_id); // HERE DWL
            
            
            if (itemList != null)
             {
                foreach (InventoryItemBase invItem in itemList)
                {
                    // this is going to break here as we're getting the entire inventory at the moment
                    // we need to filter this to just return the requested folder, this isnt very efficient
                    contents.items.Array.Add(ConvertInventoryItem(invItem, invFetch.owner_id));
                }
            }
       
            
            contents.descendents = contents.items.Array.Count;
            return reply;
        }
        
        /// <summary>
        /// Convert an internal inventory item object into an LLSD object.
        /// </summary>
        /// <param name="invItem"></param>
        /// <returns></returns>
        private LLSDInventoryItem ConvertInventoryItem(InventoryItemBase invItem, UUID ownerId)
        {
            LLSDInventoryItem llsdItem = new LLSDInventoryItem();
            llsdItem.asset_id = invItem.AssetID;
            llsdItem.created_at = invItem.CreationDate;
            llsdItem.desc = invItem.Description;
            llsdItem.flags = checked((int)invItem.Flags);
            llsdItem.item_id = invItem.ID;
            llsdItem.name = invItem.Name;
            llsdItem.parent_id = invItem.Folder;
            llsdItem.type = Enum.GetName(typeof(AssetType), invItem.AssetType).ToLower();
            llsdItem.inv_type = Enum.GetName(typeof(InventoryType), invItem.InvType).ToLower();
            llsdItem.permissions = new LLSDPermissions();
            llsdItem.permissions.creator_id = invItem.CreatorIdAsUuid; // HERE DWL
            llsdItem.permissions.base_mask = (int)invItem.CurrentPermissions;
            llsdItem.permissions.everyone_mask = (int)invItem.EveryOnePermissions;
            llsdItem.permissions.group_id = UUID.Zero;
            llsdItem.permissions.group_mask = 0;
            llsdItem.permissions.is_owner_group = false;
            llsdItem.permissions.next_owner_mask = (int)invItem.NextPermissions;
            llsdItem.permissions.owner_id = ownerId;//m_agentID; // FixMe
            llsdItem.permissions.owner_mask = (int)invItem.CurrentPermissions;
            llsdItem.sale_info = new LLSDSaleInfo();
            llsdItem.sale_info.sale_price = 10;
            llsdItem.sale_info.sale_type = "not";
            
            return llsdItem;
        }
        
      
        
        /// <summary>
        /// Returns an error message that the user could not be found in the database
        /// </summary>
        /// <returns>XML string consisting of a error element containing individual error(s)</returns>
        public XmlRpcResponse CreateUnknownUserErrorResponse()
        {
            XmlRpcResponse response = new XmlRpcResponse();
            Hashtable responseData = new Hashtable();
            responseData["error_type"] = "unknown_user";
            responseData["error_desc"] = "The user requested is not in the database";
            
            response.Value = responseData;
            return response;
        }
        
        
        public XmlRpcResponse XmlRPCCheckAuthSession(XmlRpcRequest request)
        {
            XmlRpcResponse response = new XmlRpcResponse();
            Hashtable requestData = (Hashtable) request.Params[0];
            // UserProfileData userProfile;
            
            m_log.InfoFormat("[XmlRPCCheckAuthSession]: CheckAuthSession {0}", request);
            
            string authed = "FALSE";
            if (requestData.Contains("avatar_uuid") && requestData.Contains("session_id"))
             {
                UUID guess_aid;
                UUID guess_sid;
                
                UUID.TryParse((string) requestData["avatar_uuid"], out guess_aid);
                if (guess_aid == UUID.Zero)
                 {
                    return CreateUnknownUserErrorResponse();
                }
                UUID.TryParse((string) requestData["session_id"], out guess_sid);
                if (guess_sid == UUID.Zero)
                 {
                    return CreateUnknownUserErrorResponse();
                }
                
                Agent_state_entry agentInfo = m_state_table.lookupAgentStateByUUID(guess_aid);
                
                //userProfile = m_userDataBaseService.GetUserProfile(guess_aid);
                m_log.InfoFormat("[XmlRPCCheckAuthSession]: do session ids match ? {0} == {1}", agentInfo.secureSessionID,guess_sid);
                if (agentInfo != null && agentInfo.firstName != null &&
                    agentInfo.secureSessionID == guess_sid)
                {
                    authed = "TRUE";
                }
                m_log.InfoFormat("[XmlRPCCheckAuthSession]: CheckAuthSession {1} for user {0}", guess_aid,authed);
            }
            else
             {
                m_log.InfoFormat("[XmlRPCCheckAuthSession]: CheckAuthSession FALSE");
                return CreateUnknownUserErrorResponse();
            }
            Hashtable responseData = new Hashtable();
            responseData["auth_session"] = authed;
            response.Value = responseData;
            return response;
        }



	// ------------------------------------------------------------
	// 
	// Format up a OSDMap with the contents of a InventoryItemBase
	//
	// ------------------------------------------------------------

OSDMap createItemMap(InventoryItemBase invItem)
	{
	m_log.InfoFormat("AGENT DOMAIN] Building OSDMap for item with name {0} assetID {1}",invItem.Name, invItem.AssetID);
	OSDMap responseMap = new OSDMap();
	responseMap["name"] = OSD.FromString(invItem.Name);
        responseMap["type"] = OSD.FromInteger(invItem.InvType);		
	responseMap["folder"] = OSD.FromUUID(invItem.Folder);
	responseMap["creator"] = OSD.FromUUID(invItem.CreatorIdAsUuid); // HERE DWL 
	responseMap["description"] = OSD.FromString(invItem.Description);
	responseMap["next_permissions"] = OSD.FromInteger(invItem.NextPermissions);
	m_log.InfoFormat("[AGENT DOMAIN] Item with name {0} has base permsions {1}",invItem.Name,invItem.BasePermissions);
	responseMap["base_permissions"] = OSD.FromInteger(invItem.BasePermissions);
	responseMap["everyone_permissions"] = OSD.FromInteger(invItem.EveryOnePermissions);
	responseMap["group_permissions"] = OSD.FromInteger(invItem.GroupPermissions);
        responseMap["asset_type"] = OSD.FromInteger(invItem.AssetType);
	responseMap["asset_id"] = OSD.FromUUID(invItem.AssetID);
        responseMap["group_id"] = OSD.FromUUID(invItem.GroupID);
	responseMap["group_owned"] = OSD.FromBoolean(invItem.GroupOwned);
	responseMap["sale_price"] = OSD.FromInteger(invItem.SalePrice);
	responseMap["sale_type"] = OSD.FromInteger(invItem.SaleType);
	responseMap["flags"] = OSD.FromUInteger(invItem.Flags);
	responseMap["creation_date"] = OSD.FromInteger(invItem.CreationDate);
	return responseMap;
	}

     // -----------------------------------
     //
     // Search into a folder tree
     //
     // ------------------------------------
	
InventoryItemBase locateItem(UUID itemToLocate, UUID folder)
   {
   List<InventoryItemBase> itemList = null;
   itemList = m_inventoryService.RequestFolderItems(folder); // HERE DWL 
//   itemList = m_interInventoryService.GetItemsFolder(folder); // HERE DWL 
   foreach (InventoryItemBase invItem in itemList)
      {
      m_log.InfoFormat("[AGENT DOMAIN] inventory resolver found name {0} type {1} ID {2} assetID {3} ",invItem.Name,invItem.AssetType,invItem.ID,invItem.AssetID);
      if (itemToLocate == invItem.ID)
         {
          m_log.InfoFormat("AGENT DOMAIN] inventory resolver matched ID for item with name {0} assetID {1}",invItem.Name, invItem.AssetID);
  	 return invItem;
         }
      } // Ok, if not found in the items, recurse into the folders
    List<InventoryFolderBase> folderList = null;
    folderList = m_inventoryService.RequestSubFolders(folder); // HERE DWL
//    folderList = m_interInventoryService.RequestSubFolders(folder); // HERE DWL
    InventoryItemBase tmp = null;
    foreach (InventoryFolderBase invFolder in folderList)
       {
        m_log.InfoFormat("[AGENT DOMAIN] inventory resolver found name {0} ID {1} ",invFolder.Name,invFolder.ID);
        tmp = locateItem(itemToLocate, invFolder.ID);
	if (null != tmp)
	   {
	   return tmp;
           }
       }
   return (InventoryItemBase)null;
   }
	
	// ----------------------------------------------------------
	// 
	//  Simple stub to call when we want to resolve inventory
	//
	// -----------------------------------------------------------

	public OSD adRemoteInventoryResolver( string path, OSD request, string endpoint)
	{
	m_log.InfoFormat("[AGENT DOMAIN]: remoteInventoryResolver invoked on path {0}",path);
	OSDMap requestMap = (OSDMap) request;
        OSDMap responseMap = new OSDMap(); 
        string[] pathSegments = path.Split('/'); 
        int i; 
        for (i = 0;i<pathSegments.Length;i++) 
            { 
            m_log.InfoFormat("[Agent Domain] seed cap fetch segment {0} is {1}",i,pathSegments[i]); 
            } 
        // Now we see if the cap maps to a cap we granted 
        // Look it up 
        if (pathSegments.Length < 2) 
           { 
           m_log.WarnFormat("[Agent Domain]: Badly formed seed cap fetch path {0} from endpoint {1}",path,endpoint); 
           responseMap["connect"] = OSD.FromBoolean(true); 
           return responseMap; 
          } 
        UUID agentUUID = m_uuid_table.getAgentUUIDforCap(pathSegments[3]); 
	m_log.InfoFormat("[AGENT DOMAIN]: Inventory Resolver  found agent UUID of {0}",agentUUID.ToString());
        if (agentUUID == UUID.Zero)
	   {
	   m_log.Warn("[AGENT DOMAIN]: Invalid cap");
	   return responseMap;
	   }
	// Pull out what was in the LLSD we got sent
	UUID requestAgentUUID = requestMap["agent_uuid"].AsUUID();
	if (agentUUID != requestAgentUUID)
           {
           m_log.WarnFormat("[AGENT DOMAIN:] Inventory Resolver Cap/Reuqestor UUID mismatch senders UUID is {0} Cap maps to {1}",requestAgentUUID.ToString(),agentUUID.ToString());
	   return responseMap;
	   }
        UUID requestItemID = requestMap["item_id"].AsUUID();
        m_log.InfoFormat("[AGENT DOMAIN]: Inventory Resolver is looking for and item ID of {0}",requestItemID.ToString());
	// If we're here, we have the UUID of the agent we are working for 
	// now we need to call the inventory service and map out the UUID from the Item ID
         InventoryFolderBase rootFolderB = m_inventoryService.RequestRootFolder(agentUUID); // HERE DWL
	 //InventoryFolderBase rootFolderB = new InventoryFolderBase();
         UUID rootFolder = rootFolderB.ID;
	 m_log.InfoFormat("[AGENT DOMAIN] inventory resolver got root folder of {0}", rootFolder.ToString());
	 InventoryItemBase locatedItem = locateItem(requestItemID,rootFolder);
	 if (null != locatedItem)
	    {
	     return createItemMap(locatedItem);
            }
	return responseMap;
        }
	
 // Stub to handle derez   
        public OSD derezLogoutHandler(string path, OSD request, string endpoint) 
        { 
            m_log.Info("[AGENT DOMAIN]: Derez Stub"); 
            OSDMap responseMap = new OSDMap(); 
            responseMap["connect"] = OSD.FromBoolean(true); 
            return responseMap; 
        } 


        // ---------------------------------------------------------- 
        // 
        // Micro tiny login service for the AD 
        // 
        // ---------------------------------------------------------- 
        
        
        public OSD adLoginTest( string path, OSD request, string endpoint) 
        { 
            m_log.InfoFormat("AdLoginTest called. {0}",request); 
            OSDMap requestMap = (OSDMap)request; 
            OSDMap responseMap = new OSDMap(); 
            OSDMap identMap = (OSDMap) requestMap["identifier"]; 
            string loginFirstName = identMap["first_name"].AsString(); 
            string loginLastName = identMap["last_name"].AsString(); 
	    string adURL = "";
            m_log.InfoFormat("login name is {0} {1}", loginFirstName,loginLastName); 
            string capInfix = ""; 
            UUID agentUUID = m_uuid_table.lookupAgentUUIDByName(loginFirstName,loginLastName,m_userDataBaseService); 
            m_log.InfoFormat("UUID for {0} {1} is {2}",loginFirstName,loginLastName,agentUUID); 
            Agent_state_entry storedState = m_state_table.lookupAgentStateByUUID(agentUUID); 
            string agentCapability =""; 
            switch (storedState.agentState) 
            { 
                // We think the agent is still logged in 
                // not good. We will force logot, and soon, we will de-rez the agent if he's still there 
                case AgentADState.logged_in: 
                   m_log.InfoFormat("[AGENT_DOMAIN]: AdLoginTest for user {0} {1} state was logged in",loginFirstName,loginLastName); 
                   m_state_table.updateAgentState(agentUUID,AgentADState.logged_out); 
                   m_uuid_table. breakCapLink(agentUUID); 
                   responseMap["connected"] = OSD.FromBoolean(false); 
                   responseMap["authenticated"] = OSD.FromBoolean(false); 
                   return responseMap; 
                   break;
                // OK, we know the agent and they are logged out. Get a new cap created and use it 
                case AgentADState.logged_out: 
                   m_log.InfoFormat("[AGENT_DOMAIN]: AdLoginTest for user {0} {1} state was logged out",loginFirstName,loginLastName); 
                   m_state_table.updateAgentState(agentUUID,AgentADState.logged_in); 
                   agentCapability = UUID.Random().ToString(); 
                   m_uuid_table.linkCap(agentCapability,agentUUID); 
                   adURL = "http://"+ipHostString+":"+ipHostPort+"/agent_seed_cap/"+agentCapability ; 
                   responseMap["agent_seed_capability"] = OSD.FromString(adURL); 
                   responseMap["authenticated"] = OSD.FromBoolean(true); 
                   return responseMap; 
                   break; 
                case AgentADState.unknown: 
                   m_log.InfoFormat("[AGENT_DOMAIN]: AdLoginTest for user {0} {1} state was unknown",loginFirstName,loginLastName); 
                   Agent_state_entry newState = m_state_table.createAgentEntryByName(loginFirstName,loginLastName,agentUUID); 
                   agentCapability = UUID.Random().ToString(); 
                   m_uuid_table.linkCap(agentCapability,agentUUID); 
	           adURL = "http://"+ipHostString+":"+ipHostPort+"/agent_seed_cap/"+agentCapability ; 
                   responseMap["agent_seed_capability"] = OSD.FromString(adURL); 
                   responseMap["authenticated"] = OSD.FromBoolean(true); 
                   return responseMap; 
                    break; 
                default: 
                   m_log.InfoFormat("[AGENT_DOMAIN]: AdLoginTest for user {0} {1} ERROR unknown state",loginFirstName,loginLastName); 
                   responseMap["connected"] = OSD.FromBoolean(false); 
                   responseMap["authenticated"] = OSD.FromBoolean(false); 
                   m_state_table.updateAgentState(agentUUID,AgentADState.logged_out); 
                   return responseMap;
                break; 
            }           
            adURL = "http://"+ipHostString+":"+ipHostPort+"/agent_seed_cap/"+agentCapability ; 
            responseMap["agent_seed_capability"] = OSD.FromString(adURL); 
            responseMap["authenticated"] = OSD.FromBoolean(true); 
            return responseMap; 
        } 
        
        
        
        // -------------------------------------------------------------------- 
        // 
        // Handle the seed cap fetch, validating info from the caps segment and 
        // mapping it to the request 
        // 
        // --------------------------------------------------------------------- 
        
        public OSD adSeedCapFetch(string path, OSD request, string endpoint) 
        { 
            m_log.Info("Ad Seed Cap fetch called"); 
            m_log.InfoFormat("[Agent Domain]: ad SeedCapFetch endpoint is {0}",endpoint); 
            m_log.InfoFormat("[Agent Domain]: ad path was {0}",path); 
            OSDMap responseMap = new OSDMap(); 
            string[] pathSegments = path.Split('/'); 
            int i; 
            for (i = 0;i<pathSegments.Length;i++) 
            { 
                m_log.InfoFormat("[Agent Domain] seed cap fetch segment {0} is {1}",i,pathSegments[i]); 
            } 
            // Now we see if the cap maps to a cap we granted 
            // Look it up 
            if (pathSegments.Length < 2) 
             { 
                m_log.WarnFormat("[Agent Domain]: Badly formed seed cap fetch path {0} from endpoint {1}",path,endpoint); 
                responseMap["connect"] = OSD.FromBoolean(true); 
                return responseMap; 
            } 
            UUID agentUUID = m_uuid_table.getAgentUUIDforCap(pathSegments[2]); 
            if (UUID.Zero == agentUUID) 
             if (pathSegments.Length < 2) 
             { 
                m_log.WarnFormat("[Agent Domain]: invalid seed cap fetch path {0} from endpoint {1}",path,endpoint); 
                responseMap["connect"] = OSD.FromBoolean(true); 
                return responseMap; 
            } 
            string capSufix = pathSegments[2]; 
            Agent_state_entry agentState = m_state_table.lookupAgentStateByUUID(agentUUID); 
            // We assume the above call always works, it will, but it may hold invalid data. 
            // We should revisit this. 
            responseMap["age_verified"] = OSD.FromBoolean(true); 
            responseMap["agent_access"] = OSD.FromString("M"); 
            OSDMap capMap = new OSDMap(); 
            string adPrefix = "http://"+ipHostString+":"+ipHostPort ; 
            capMap["agent/info"] = OSD.FromString(adPrefix+"/cap/agent/info/"+capSufix); 
	    capMap["agent/inventory-skeleton"] = OSD.FromString(adPrefix+"/agent/inventory/skeleton/"+capSufix);
            capMap["event_queue"] = OSD.FromString(adPrefix+"/cap/event_queue/"+capSufix); 
            capMap["rez_avatar/place"] = OSD.FromString(adPrefix+"/cap/rez_avatar/place/"+capSufix); 
            
            // TODO Rob add the WebFetchInventoryDescendents Cap here 
            capMap["agent/inventory"] = OSD.FromString(adPrefix+"/cap/agent/inventory/"+capSufix);
            m_log.InfoFormat("WebFetchInventoryDescendents cap url is {0}", adPrefix+"/cap/agent/inventory/"+capSufix);
            
            // TODO Rob need to track and remove this handler on derez
            m_httpServer.AddStreamHandler(new RestStreamHandler("POST", "/cap/agent/inventory/"+capSufix, webFetchInventoryDescendentsRequest));
            
            responseMap["capabilities"] = capMap; 
            responseMap["first_name"] = OSD.FromString(agentState.firstName); 
            responseMap["last_name"] = OSD.FromString(agentState.lastName); 
            responseMap["identified"] = OSD.FromBoolean(true); 
            responseMap["limited_to_estate"] = OSD.FromInteger(1); 
            responseMap["transacted"] = OSD.FromBoolean(true); 
            responseMap["god_level"] = OSD.FromInteger(0); 
            return responseMap; 
        }

   public OSD createInventory(string path, OSD request, string endpoint)
        {
            m_log.Info("[AGENT DOMAIN]: createInventory cap called"); 
            
            OSDMap responseMap = new OSDMap(); 
            OSDMap requestMap = (OSDMap)request;
            
            // check that this cap is valid
            string[] pathSegments = path.Split('/'); 
            int i; 
            
            if (pathSegments.Length < 2) 
             { 
                m_log.WarnFormat("[Agent Domain]: createInventory Badly formed seed cap fetch path {0} from endpoint {1}",path,endpoint); 
                responseMap["connect"] = OSD.FromBoolean(true); 
                return responseMap; 
            } 
            
            UUID agentUUID = m_uuid_table.getAgentUUIDforCap(pathSegments[3]); 
            m_log.InfoFormat("[AGENT DOMAIN]: Inventory Reflector  found agent UUID of {0}",agentUUID.ToString());
            if (agentUUID == UUID.Zero)
             {
                m_log.Warn("[AGENT DOMAIN]: Invalid cap");
                return responseMap;
            }
            // Pull out what was in the LLSD we got sent
            UUID requestAgentUUID = requestMap["Owner"].AsUUID();
            if (agentUUID != requestAgentUUID)
             {
                m_log.WarnFormat("[AGENT DOMAIN:] Inventory Resolver Cap/Reuqestor UUID mismatch senders UUID is {0} Cap maps to {1}",requestAgentUUID.ToString(),agentUUID.ToString());
                return responseMap;
            }
            
            InventoryItemBase resolvedItem = OSDToInventoryItemBase(requestMap);
            
            if(String.IsNullOrEmpty(resolvedItem.CreatorId) || resolvedItem.CreatorId.Equals(UUID.Zero.ToString())  )
            {
            	resolvedItem.CreatorId = agentUUID.ToString();
            	m_log.Info("[AGENT DOMAIN]: createInventory, CreatorId not set setting to " + agentUUID.ToString());	
            }
			
			// call the inventory service to add it 
			bool success = m_inventoryService.AddItem( resolvedItem);            
            
            responseMap["success"] = OSD.FromBoolean(success);
            return responseMap;
            
        }
        
        
        
        
        public OSD updateInventory(string path, OSD request, string endpoint)
        {
            m_log.Info("[AGENT DOMAIN]: updateInventory cap called"); 
            
            OSDMap responseMap = new OSDMap(); 
            OSDMap requestMap = (OSDMap)request;
            
            // check that this cap is valid
            string[] pathSegments = path.Split('/'); 
            int i; 
            
            if (pathSegments.Length < 2) 
             { 
                m_log.WarnFormat("[Agent Domain]: updateInventory Badly formed seed cap fetch path {0} from endpoint {1}",path,endpoint); 
                responseMap["connect"] = OSD.FromBoolean(true); 
                return responseMap; 
            } 
            
            UUID agentUUID = m_uuid_table.getAgentUUIDforCap(pathSegments[3]); 
            m_log.InfoFormat("[AGENT DOMAIN]: updateInventory  found agent UUID of {0}",agentUUID.ToString());
            if (agentUUID == UUID.Zero)
             {
                m_log.Warn("[AGENT DOMAIN]: Invalid cap");
                return responseMap;
            }
            
            m_log.Info("[AGENT DOMAIN]: 1");
            InventoryItemBase itemUpd = OSDToInventoryItemBase(requestMap);
            m_log.Info("[AGENT DOMAIN]: 2");
            // we need to get the item from inventory as the full item details do not get passed to us in the cap
            InventoryItemBase item = m_inventoryService.GetInventoryItem(itemUpd.ID);
            m_log.Info("[AGENT DOMAIN]: 3");
            if (item != null)
            {
            	item.Name = itemUpd.Name;
                item.Description = itemUpd.Description;
                item.NextPermissions = itemUpd.NextPermissions;
                item.CurrentPermissions = itemUpd.CurrentPermissions; 
                item.EveryOnePermissions = itemUpd.EveryOnePermissions;
                item.GroupPermissions = itemUpd.GroupPermissions;

                item.GroupID = itemUpd.GroupID;
                item.GroupOwned = itemUpd.GroupOwned;
                item.CreationDate = itemUpd.CreationDate;
                // The client sends zero if its newly created?

                if (itemUpd.CreationDate == 0)
                    item.CreationDate = Util.UnixTimeSinceEpoch();
                else
                    item.CreationDate = itemUpd.CreationDate;

                item.InvType = itemUpd.InvType;
                item.SalePrice = itemUpd.SalePrice;
            	item.SaleType = itemUpd.SaleType;
                item.Flags = itemUpd.Flags;
                // the assettype and assetid always come across as 000 curently
                //m_log.Info("[AGENT DOMAIN]: updateInventory assettype is "+itemUpd.AssetType + "assetid is " + itemUpd.AssetID);
                //item.AssetType = itemUpd.AssetType;
                //item.AssetID = itemUpd.AssetID;
            }
			m_log.Info("[AGENT DOMAIN]: 4");
			// call the inventory service to add it 
			bool success = m_inventoryService.UpdateItem( item);
            m_log.Info("[AGENT DOMAIN]: 5");
            
            responseMap["success"] = OSD.FromBoolean(success);
            return responseMap;
            
        }
        
        public OSD updateWearables(string path, OSD request, string endpoint)
        {
        	m_log.Info("[AGENT DOMAIN]: updateWearables cap called");
        	
        	 OSDMap responseMap = new OSDMap(); 
            OSDMap requestMap = (OSDMap)request;
            
            // check that this cap is valid
            string[] pathSegments = path.Split('/'); 
            int i; 
            
            if (pathSegments.Length < 2) 
             { 
                m_log.WarnFormat("[Agent Domain]: updateInventory Badly formed seed cap fetch path {0} from endpoint {1}",path,endpoint); 
                responseMap["connect"] = OSD.FromBoolean(true); 
                return responseMap; 
            } 
            
            UUID agentUUID = m_uuid_table.getAgentUUIDforCap(pathSegments[4]); 
            m_log.InfoFormat("[AGENT DOMAIN]: updateWearables  found agent UUID of {0}",agentUUID.ToString());
            if (agentUUID == UUID.Zero)
             {
                m_log.Warn("[AGENT DOMAIN]: Invalid cap");
                return responseMap;
            }
            
            OSDArray wearables = (OSDArray)requestMap["avatar_wearables"];
	        AvatarWearable[] wearableArray = new AvatarWearable[13];  
            int count =0;
            InventoryItemBase item;
            foreach (OSDMap wble in wearables)
            {
            	 item = m_inventoryService.GetInventoryItem(wble["ItemID"].AsUUID());
            	 AvatarWearable aWearable;
            	 if(item!=null)
            	 {
                 	aWearable = new AvatarWearable(wble["ItemID"].AsUUID(), item.AssetID);
            	 }
                 else
                 {
                 	AvatarAppearance def = new AvatarAppearance();
                 	aWearable = new AvatarWearable(wble["ItemID"].AsUUID(), def.Wearables[count].AssetID);
                 }
                 wearableArray[count] = aWearable;
                 count++;
            }  
            
            AvatarAppearance appearance = m_userDataBaseService.GetUserAppearance(agentUUID);
            appearance.Wearables = wearableArray;
            m_userDataBaseService.UpdateUserAppearance(agentUUID,appearance);
            
            responseMap["success"] = OSD.FromBoolean(true);
            return responseMap;
        	
        }

	public OSD adGetSkeleton(string path, OSD request, string endpoint)
        {
        m_log.Info("[AGENT DOMAIN]: adGetSkeleton called");
	OSDMap responseMap = new OSDMap(); 
            OSDMap requestMap = (OSDMap)request;
            
            // check that this cap is valid
            string[] pathSegments = path.Split('/'); 
            int i; 
            
            if (pathSegments.Length < 2) 
             { 
                m_log.WarnFormat("[Agent Domain]: moveInventory Badly formed seed cap fetch path {0} from endpoint {1}",path,endpoint); 
                responseMap["connect"] = OSD.FromBoolean(true); 
                return responseMap; 
            } 
            
            UUID agentUUID = m_uuid_table.getAgentUUIDforCap(pathSegments[4]); 
            m_log.InfoFormat("[AGENT DOMAIN]: moveInventory  found agent UUID of {0}",agentUUID.ToString());
            if (agentUUID == UUID.Zero)
             {
                m_log.Warn("[AGENT DOMAIN]: Invalid cap");
                return responseMap;
              }
        OSD skel =  getInventorySkeleton(agentUUID);
	responseMap["Skeleton"] = skel;
	return responseMap;
        }
        
        public OSD moveInventory(string path, OSD request, string endpoint)
        {
            m_log.Info("[AGENT DOMAIN]: moveInventory cap called"); 
            
            OSDMap responseMap = new OSDMap(); 
            OSDMap requestMap = (OSDMap)request;
            
            // check that this cap is valid
            string[] pathSegments = path.Split('/'); 
            int i; 
            
            if (pathSegments.Length < 2) 
             { 
                m_log.WarnFormat("[Agent Domain]: moveInventory Badly formed seed cap fetch path {0} from endpoint {1}",path,endpoint); 
                responseMap["connect"] = OSD.FromBoolean(true); 
                return responseMap; 
            } 
            
            UUID agentUUID = m_uuid_table.getAgentUUIDforCap(pathSegments[3]); 
            m_log.InfoFormat("[AGENT DOMAIN]: moveInventory  found agent UUID of {0}",agentUUID.ToString());
            if (agentUUID == UUID.Zero)
             {
                m_log.Warn("[AGENT DOMAIN]: Invalid cap");
                return responseMap;
            }
            
            InventoryItemBase resolvedItem = OSDToInventoryItemBase(requestMap);
            
			
			// call the inventory service to add it 
			bool success = m_inventoryService.UpdateItem( resolvedItem);
			//bool success = false; // DWL HACK
            
            
            responseMap["success"] = OSD.FromBoolean(success);
            return responseMap;
            
        }
        
        public OSD deleteInventory(string path, OSD request, string endpoint)
        {
            m_log.Info("[AGENT DOMAIN]: deleteInventory cap called"); 
            
            OSDMap responseMap = new OSDMap(); 
            OSDMap requestMap = (OSDMap)request;
            
            // check that this cap is valid
            string[] pathSegments = path.Split('/'); 
            int i; 
            
            if (pathSegments.Length < 2) 
             { 
                m_log.WarnFormat("[Agent Domain]: deleteInventory Badly formed seed cap fetch path {0} from endpoint {1}",path,endpoint); 
                responseMap["connect"] = OSD.FromBoolean(true); 
                return responseMap; 
            } 
            
            UUID agentUUID = m_uuid_table.getAgentUUIDforCap(pathSegments[3]); 
            m_log.InfoFormat("[AGENT DOMAIN]: deleteInventory  found agent UUID of {0}",agentUUID.ToString());
            if (agentUUID == UUID.Zero)
             {
                m_log.Warn("[AGENT DOMAIN]: Invalid cap");
                return responseMap;
            }
            
            InventoryItemBase resolvedItem = OSDToInventoryItemBase(requestMap);
			
			// call the inventory service to add it 
			bool success = m_inventoryService.DeleteItem( resolvedItem);
			//bool success = false;
            
            
            responseMap["success"] = OSD.FromBoolean(success);
            return responseMap;
            
        }

            
        
        public OSD createInventoryFolder(string path, OSD request, string endpoint)
        {
            m_log.Info("[AGENT DOMAIN]: createInventoryFolder cap called"); 
            
            OSDMap responseMap = new OSDMap(); 
            OSDMap requestMap = (OSDMap)request;
            
            // check that this cap is valid
            string[] pathSegments = path.Split('/'); 
            int i; 
            
            if (pathSegments.Length < 2) 
             { 
                m_log.WarnFormat("[Agent Domain]: createInventoryFolder Badly formed seed cap fetch path {0} from endpoint {1}",path,endpoint); 
                responseMap["connect"] = OSD.FromBoolean(true); 
                return responseMap; 
            } 
            
            UUID agentUUID = m_uuid_table.getAgentUUIDforCap(pathSegments[3]); 
            m_log.InfoFormat("[AGENT DOMAIN]: createInventoryFolder  found agent UUID of {0}",agentUUID.ToString());
            if (agentUUID == UUID.Zero)
             {
                m_log.Warn("[AGENT DOMAIN]: Invalid cap");
                return responseMap;
            }
            
            InventoryFolderBase folder = OSDToInventoryFolderBase(requestMap);
            bool success = m_inventoryService.AddFolder( folder);
            //bool success = false; // DWL HACK
            responseMap["success"] = OSD.FromBoolean(success);
            return responseMap;        
        }
        
        public OSD updateInventoryFolder(string path, OSD request, string endpoint)
        {
            m_log.Info("[AGENT DOMAIN]: updateInventoryFolder cap called"); 
            
            OSDMap responseMap = new OSDMap(); 
            OSDMap requestMap = (OSDMap)request;
            
            // check that this cap is valid
            string[] pathSegments = path.Split('/'); 
            int i; 
            
            if (pathSegments.Length < 2) 
             { 
                m_log.WarnFormat("[Agent Domain]: updateInventoryFolder Badly formed seed cap fetch path {0} from endpoint {1}",path,endpoint); 
                responseMap["connect"] = OSD.FromBoolean(true); 
                return responseMap; 
            } 
            
            UUID agentUUID = m_uuid_table.getAgentUUIDforCap(pathSegments[3]); 
            m_log.InfoFormat("[AGENT DOMAIN]: updateInventoryFolder  found agent UUID of {0}",agentUUID.ToString());
            
            if (agentUUID == UUID.Zero)
            {
                m_log.Warn("[AGENT DOMAIN]: Invalid cap");
                return responseMap;
            }
            
            InventoryFolderBase folder = OSDToInventoryFolderBase(requestMap);
            bool success = m_inventoryService.UpdateFolder( folder);
	    //bool success = false; // DWL HACK
            responseMap["success"] = OSD.FromBoolean(success);
            return responseMap;        
        }
        
        public OSD moveInventoryFolder(string path, OSD request, string endpoint)
        {
            m_log.Info("[AGENT DOMAIN]: moveInventoryFolder cap called"); 
            OSDMap responseMap = new OSDMap(); 
            OSDMap requestMap = (OSDMap)request;
            
            // check that this cap is valid
            string[] pathSegments = path.Split('/'); 
            int i; 
            
            if (pathSegments.Length < 2) 
             { 
                m_log.WarnFormat("[Agent Domain]: moveInventoryFolder Badly formed seed cap fetch path {0} from endpoint {1}",path,endpoint); 
                responseMap["connect"] = OSD.FromBoolean(true); 
                return responseMap; 
            } 
            
            UUID agentUUID = m_uuid_table.getAgentUUIDforCap(pathSegments[3]); 
            m_log.InfoFormat("[AGENT DOMAIN]: moveInventoryFolder  found agent UUID of {0}",agentUUID.ToString());
            if (agentUUID == UUID.Zero)
             {
                m_log.Warn("[AGENT DOMAIN]: Invalid cap");
                return responseMap;
            }
                        
            
            InventoryFolderBase folder = OSDToInventoryFolderBase(requestMap);
            bool success = m_inventoryService.MoveFolder( folder);
            //bool success = false; // DWL HACK
            responseMap["success"] = OSD.FromBoolean(success);
            return responseMap;        
        }
        
        public OSD deleteInventoryFolder(string path, OSD request, string endpoint)
        {
            m_log.Info("[AGENT DOMAIN]: deleteInventoryFolder cap called"); 
            OSDMap responseMap = new OSDMap(); 
            OSDMap requestMap = (OSDMap)request;
                        
            // check that this cap is valid
            string[] pathSegments = path.Split('/'); 
            int i; 
            
            if (pathSegments.Length < 2) 
             { 
                m_log.WarnFormat("[Agent Domain]: deleteInventory Badly formed seed cap fetch path {0} from endpoint {1}",path,endpoint); 
                responseMap["connect"] = OSD.FromBoolean(true); 
                return responseMap; 
            } 
            
            UUID agentUUID = m_uuid_table.getAgentUUIDforCap(pathSegments[3]); 
            m_log.InfoFormat("[AGENT DOMAIN]: deleteInventory  found agent UUID of {0}",agentUUID.ToString());
            if (agentUUID == UUID.Zero)
             {
                m_log.Warn("[AGENT DOMAIN]: Invalid cap");
                return responseMap;
            }
            
            InventoryFolderBase folder = OSDToInventoryFolderBase(requestMap);
            bool success = m_inventoryService.PurgeFolder( folder);
            //bool success = false; // DWL HACK
            responseMap["success"] = OSD.FromBoolean(success);
            return responseMap;        
        }
        
        
        private InventoryItemBase OSDToInventoryItemBase(OSDMap requestMap)
        {
        	InventoryItemBase resolvedItem = new InventoryItemBase();
//          resolvedItem.Creator =   requestMap["Creator"].AsUUID(); // DWL HACK
            resolvedItem.Owner =   requestMap["Owner"].AsUUID();
            resolvedItem.GroupID = requestMap["GroupID"].AsUUID();
            resolvedItem.ID = requestMap["ID"].AsUUID();
            resolvedItem.AssetID =   requestMap["AssetID"].AsUUID();
            resolvedItem.AssetType= requestMap["AssetType"].AsInteger();
            resolvedItem.Folder =   requestMap["Folder"].AsUUID();
            resolvedItem.Name = requestMap["Name"].AsString();
            resolvedItem.Description= requestMap["Description"].AsString();
            resolvedItem.NextPermissions = (uint) requestMap["NextPermissions"].AsInteger();
            resolvedItem.BasePermissions = (uint) requestMap["BasePermissions"].AsInteger();
            resolvedItem.CurrentPermissions = (uint) requestMap["CurrentPermissions"].AsInteger();
		    resolvedItem.EveryOnePermissions = (uint) requestMap["EveryOnePermissions"].AsInteger();
		    resolvedItem.GroupPermissions = (uint) requestMap["GroupPermissions"].AsInteger();
	        resolvedItem.InvType = requestMap["InvType"].AsInteger();
			resolvedItem.SalePrice = requestMap["SalePrice"].AsInteger();
			resolvedItem.SaleType = (byte) requestMap["SaleType"].AsInteger();
			resolvedItem.CreationDate = requestMap["CreationDate"].AsInteger();
			resolvedItem.GroupOwned = requestMap["GroupOwned"].AsBoolean();
			resolvedItem.Flags =  (uint)requestMap["Flags"].AsInteger();
			
			return resolvedItem;
        }
        
        private InventoryFolderBase OSDToInventoryFolderBase(OSDMap requestMap)
        {
        	InventoryFolderBase folder = new InventoryFolderBase();
        	folder.Name = requestMap["Name"].AsString();
        	folder.ID = requestMap["ID"].AsUUID();
        	folder.Owner = requestMap["Owner"].AsUUID();
        	folder.ParentID = requestMap["ParentID"].AsUUID();
        	folder.Type = (short)requestMap["Type"].AsInteger();
        	folder.Version = (ushort)requestMap["Version"].AsInteger();
        	        	
			
			return folder;
        }




// END AD CODE















        public void RegisterHandlers(BaseHttpServer httpServer, bool registerLLSDHandler, bool registerOpenIDHandlers)
        {
            m_httpServer = httpServer;

            m_httpServer.AddXmlRPCHandler("login_to_simulator", XmlRpcLoginMethod);
            m_httpServer.AddHTTPHandler("login", ProcessHTMLLogin);
            m_httpServer.AddXmlRPCHandler("set_login_params", XmlRPCSetLoginParams);
            m_httpServer.AddXmlRPCHandler("check_auth_session", XmlRPCCheckAuthSession, false);

            if (registerLLSDHandler)
            {
                m_httpServer.SetDefaultLLSDHandler(LLSDLoginMethod);
            }

            if (registerOpenIDHandlers)
            {
                // Handler for OpenID avatar identity pages
                m_httpServer.AddStreamHandler(new OpenIdStreamHandler("GET", "/users/", this));
                // Handlers for the OpenID endpoint server
                m_httpServer.AddStreamHandler(new OpenIdStreamHandler("POST", "/openid/server/", this));
                m_httpServer.AddStreamHandler(new OpenIdStreamHandler("GET", "/openid/server/", this));
            }
	    m_log.Info("[Agent Domain]: Adding LLSDHandlers");
            
            m_httpServer.AddLLSDHandler("/login", adLoginTest);
            m_httpServer.AddLLSDHandler("/agent_seed_cap", adSeedCapFetch);
            m_httpServer.AddLLSDHandler("/agent/inventory_resolver", adRemoteInventoryResolver);
            m_httpServer.AddLLSDHandler("/agent/info", adAgentInfoStub);
            m_httpServer.AddLLSDHandler("/cap/event_queue/", adEventQueueStub);
            m_httpServer.AddLLSDHandler("/cap/rez_avatar/place", adRezAvatarPlace);
            m_httpServer.AddLLSDHandler("/cap/rez_avatar/derez",derezLogoutHandler);

	    // David for skeleton
	    m_httpServer.AddLLSDHandler("/agent/inventory/skeleton",adGetSkeleton);
            
            // [Rob] for inventory reflector
            m_httpServer.AddLLSDHandler("/agent/inventory_create",createInventory);
            m_httpServer.AddLLSDHandler("/agent/inventory_update",updateInventory);
            m_httpServer.AddLLSDHandler("/agent/inventory_move",moveInventory);
            m_httpServer.AddLLSDHandler("/agent/inventory_delete",deleteInventory);
            //m_httpServer.AddLLSDHandler("/agent/inventory_copy",updateInventory);
            
            m_httpServer.AddLLSDHandler("/agent/inventoryfolder_create",createInventoryFolder);
            m_httpServer.AddLLSDHandler("/agent/inventoryfolder_update",updateInventoryFolder);
            m_httpServer.AddLLSDHandler("/agent/inventoryfolder_move",moveInventoryFolder);
            m_httpServer.AddLLSDHandler("/agent/inventoryfolder_delete",deleteInventoryFolder);
            
            m_httpServer.AddLLSDHandler("/agent/wearables/update",updateWearables);
            
            // TODO Rob this is in the wrong place, but will work here for now
            // we need this so the inventory service can call us to verify the session.
            m_httpServer.AddXmlRPCHandler("check_auth_session", XmlRPCCheckAuthSession);
            
        }


        public void setloginlevel(int level)
        {
            m_minLoginLevel = level;
            m_log.InfoFormat("[GRID]: Login Level set to {0} ", level);
        }
        public void setwelcometext(string text)
        {
            m_welcomeMessage = text;
            m_log.InfoFormat("[GRID]: Login text  set to {0} ", text);
        }

        public override void LogOffUser(UserProfileData theUser, string message)
        {
            RegionProfileData SimInfo;
            try
            {
                SimInfo = m_regionProfileService.RequestSimProfileData(
                    theUser.CurrentAgent.Handle, m_config.GridServerURL,
                    m_config.GridSendKey, m_config.GridRecvKey);

                if (SimInfo == null)
                {
                    m_log.Error("[GRID]: Region user was in isn't currently logged in");
                    return;
                }
            }
            catch (Exception)
            {
                m_log.Error("[GRID]: Unable to look up region to log user off");
                return;
            }

            // Prepare notification
            Hashtable SimParams = new Hashtable();
            SimParams["agent_id"] = theUser.ID.ToString();
            SimParams["region_secret"] = theUser.CurrentAgent.SecureSessionID.ToString();
            SimParams["region_secret2"] = SimInfo.regionSecret;
            //m_log.Info(SimInfo.regionSecret);
            SimParams["regionhandle"] = theUser.CurrentAgent.Handle.ToString();
            SimParams["message"] = message;
            ArrayList SendParams = new ArrayList();
            SendParams.Add(SimParams);

            m_log.InfoFormat(
                "[ASSUMED CRASH]: Telling region {0} @ {1},{2} ({3}) that their agent is dead: {4}",
                SimInfo.regionName, SimInfo.regionLocX, SimInfo.regionLocY, SimInfo.httpServerURI,
                theUser.FirstName + " " + theUser.SurName);

            try
            {
                XmlRpcRequest GridReq = new XmlRpcRequest("logoff_user", SendParams);
                XmlRpcResponse GridResp = GridReq.Send(SimInfo.httpServerURI, 6000);

                if (GridResp.IsFault)
                {
                    m_log.ErrorFormat(
                        "[LOGIN]: XMLRPC request for {0} failed, fault code: {1}, reason: {2}, This is likely an old region revision.",
                        SimInfo.httpServerURI, GridResp.FaultCode, GridResp.FaultString);
                }
            }
            catch (Exception)
            {
                m_log.Error("[LOGIN]: Error telling region to logout user!");
            }

            // Prepare notification
            SimParams = new Hashtable();
            SimParams["agent_id"] = theUser.ID.ToString();
            SimParams["region_secret"] = SimInfo.regionSecret;
            //m_log.Info(SimInfo.regionSecret);
            SimParams["regionhandle"] = theUser.CurrentAgent.Handle.ToString();
            SimParams["message"] = message;
            SendParams = new ArrayList();
            SendParams.Add(SimParams);

            m_log.InfoFormat(
                "[ASSUMED CRASH]: Telling region {0} @ {1},{2} ({3}) that their agent is dead: {4}",
                SimInfo.regionName, SimInfo.regionLocX, SimInfo.regionLocY, SimInfo.httpServerURI,
                theUser.FirstName + " " + theUser.SurName);

            try
            {
                XmlRpcRequest GridReq = new XmlRpcRequest("logoff_user", SendParams);
                XmlRpcResponse GridResp = GridReq.Send(SimInfo.httpServerURI, 6000);

                if (GridResp.IsFault)
                {
                    m_log.ErrorFormat(
                        "[LOGIN]: XMLRPC request for {0} failed, fault code: {1}, reason: {2}, This is likely an old region revision.",
                        SimInfo.httpServerURI, GridResp.FaultCode, GridResp.FaultString);
                }
            }
            catch (Exception)
            {
                m_log.Error("[LOGIN]: Error telling region to logout user!");
            }
            //base.LogOffUser(theUser);
        }

        protected override RegionInfo RequestClosestRegion(string region)
        {
            RegionProfileData profileData = m_regionProfileService.RequestSimProfileData(region,
                                                                                         m_config.GridServerURL, m_config.GridSendKey, m_config.GridRecvKey);

            if (profileData != null)
            {
                return profileData.ToRegionInfo();
            }
            else
            {
                return null;
            }
        }

        protected override RegionInfo GetRegionInfo(ulong homeRegionHandle)
        {
            RegionProfileData profileData = m_regionProfileService.RequestSimProfileData(homeRegionHandle,
                                                                                         m_config.GridServerURL, m_config.GridSendKey,
                                                                                         m_config.GridRecvKey);
            if (profileData != null)
            {
                return profileData.ToRegionInfo();
            }
            else
            {
                return null;
            }
        }

        protected override RegionInfo GetRegionInfo(UUID homeRegionId)
        {
            RegionProfileData profileData = m_regionProfileService.RequestSimProfileData(homeRegionId,
                                                                                         m_config.GridServerURL, m_config.GridSendKey,
                                                                                         m_config.GridRecvKey);
            if (profileData != null)
            {
                return profileData.ToRegionInfo();
            }
            else
            {
                return null;
            }
        }

        protected override bool PrepareLoginToRegion(RegionInfo regionInfo, UserProfileData user, LoginResponse response, IPEndPoint remoteClient)
        {
            return PrepareLoginToRegion(RegionProfileData.FromRegionInfo(regionInfo), user, response, remoteClient);
        }

        /// <summary>
        /// Prepare a login to the given region.  This involves both telling the region to expect a connection
        /// and appropriately customising the response to the user.
        /// </summary>
        /// <param name="regionInfo"></param>
        /// <param name="user"></param>
        /// <param name="response"></param>
        /// <returns>true if the region was successfully contacted, false otherwise</returns>
        private bool PrepareLoginToRegion(RegionProfileData regionInfo, UserProfileData user, LoginResponse response, IPEndPoint remoteClient)
        {
            try
            {
                response.SimAddress = Util.GetHostFromURL(regionInfo.serverURI).ToString();
                response.SimPort = uint.Parse(regionInfo.serverURI.Split(new char[] { '/', ':' })[4]);
                response.RegionX = regionInfo.regionLocX;
                response.RegionY = regionInfo.regionLocY;

                string capsPath = CapsUtil.GetRandomCapsObjectPath();

                // Adam's working code commented for now -- Diva 5/25/2009
                //// For NAT
                ////string host = NetworkUtil.GetHostFor(remoteClient.Address, regionInfo.ServerIP);
                //string host = response.SimAddress;
                //// TODO: This doesnt support SSL. -Adam
                //string serverURI = "http://" + host + ":" + regionInfo.ServerPort;

                //response.SeedCapability = serverURI + CapsUtil.GetCapsSeedPath(capsPath);

                // Take off trailing / so that the caps path isn't //CAPS/someUUID
                if (regionInfo.httpServerURI.EndsWith("/"))
                    regionInfo.httpServerURI = regionInfo.httpServerURI.Substring(0, regionInfo.httpServerURI.Length - 1);
                response.SeedCapability = regionInfo.httpServerURI + CapsUtil.GetCapsSeedPath(capsPath);


                // Notify the target of an incoming user
                m_log.InfoFormat(
                    "[LOGIN]: Telling {0} @ {1},{2} ({3}) to prepare for client connection",
                    regionInfo.regionName, response.RegionX, response.RegionY, regionInfo.httpServerURI);

                // Update agent with target sim
                user.CurrentAgent.Region = regionInfo.UUID;
                user.CurrentAgent.Handle = regionInfo.regionHandle;

                // Prepare notification
                Hashtable loginParams = new Hashtable();
                loginParams["session_id"] = user.CurrentAgent.SessionID.ToString();
                loginParams["secure_session_id"] = user.CurrentAgent.SecureSessionID.ToString();
                loginParams["firstname"] = user.FirstName;
                loginParams["lastname"] = user.SurName;
                loginParams["agent_id"] = user.ID.ToString();
                loginParams["circuit_code"] = (Int32)Convert.ToUInt32(response.CircuitCode);
                loginParams["startpos_x"] = user.CurrentAgent.Position.X.ToString();
                loginParams["startpos_y"] = user.CurrentAgent.Position.Y.ToString();
                loginParams["startpos_z"] = user.CurrentAgent.Position.Z.ToString();
                loginParams["regionhandle"] = user.CurrentAgent.Handle.ToString();
                loginParams["caps_path"] = capsPath;

                // Get appearance
                AvatarAppearance appearance = m_userManager.GetUserAppearance(user.ID);
                if (appearance != null)
                {
                    loginParams["appearance"] = appearance.ToHashTable();
                    m_log.DebugFormat("[LOGIN]: Found appearance for {0} {1}", user.FirstName, user.SurName);
                }
                else
                {
                    m_log.DebugFormat("[LOGIN]: Appearance not for {0} {1}. Creating default.", user.FirstName, user.SurName);
                    appearance = new AvatarAppearance(user.ID);
                }

                ArrayList SendParams = new ArrayList();
                SendParams.Add(loginParams);

                // Send
                XmlRpcRequest GridReq = new XmlRpcRequest("expect_user", SendParams);
                XmlRpcResponse GridResp = GridReq.Send(regionInfo.httpServerURI, 6000);

                if (!GridResp.IsFault)
                {
                    bool responseSuccess = true;

                    if (GridResp.Value != null)
                    {
                        Hashtable resp = (Hashtable)GridResp.Value;
                        if (resp.ContainsKey("success"))
                        {
                            if ((string)resp["success"] == "FALSE")
                            {
                                responseSuccess = false;
                            }
                        }
                    }
                    
                    if (responseSuccess)
                    {
                        handlerUserLoggedInAtLocation = OnUserLoggedInAtLocation;
                        if (handlerUserLoggedInAtLocation != null)
                        {
                            handlerUserLoggedInAtLocation(user.ID, user.CurrentAgent.SessionID,
                                                          user.CurrentAgent.Region,
                                                          user.CurrentAgent.Handle,
                                                          user.CurrentAgent.Position.X,
                                                          user.CurrentAgent.Position.Y,
                                                          user.CurrentAgent.Position.Z,
                                                          user.FirstName, user.SurName);
                        }
                    }
                    else
                    {
                        m_log.ErrorFormat("[LOGIN]: Region responded that it is not available to receive clients");
                        return false;
                    }
                }
                else
                {
                    m_log.ErrorFormat("[LOGIN]: XmlRpc request to region failed with message {0}, code {1} ", GridResp.FaultString, GridResp.FaultCode);
                    return false;
                }
            }
            catch (Exception e)
            {
                m_log.ErrorFormat("[LOGIN]: Region not available for login, {0}", e);
                return false;
            }

            return true;
        }

        public XmlRpcResponse XmlRPCSetLoginParams(XmlRpcRequest request, IPEndPoint remoteClient)
        {
            XmlRpcResponse response = new XmlRpcResponse();
            Hashtable requestData = (Hashtable)request.Params[0];
            UserProfileData userProfile;
            Hashtable responseData = new Hashtable();

            UUID uid;
            string pass = requestData["password"].ToString();

            if (!UUID.TryParse((string)requestData["avatar_uuid"], out uid))
            {
                responseData["error"] = "No authorization";
                response.Value = responseData;
                return response;
            }

            userProfile = m_userManager.GetUserProfile(uid);

            if (userProfile == null ||
                (!AuthenticateUser(userProfile, pass)) ||
                userProfile.GodLevel < 200)
            {
                responseData["error"] = "No authorization";
                response.Value = responseData;
                return response;
            }

            if (requestData.ContainsKey("login_level"))
            {
                m_minLoginLevel = Convert.ToInt32(requestData["login_level"]);
            }

            if (requestData.ContainsKey("login_motd"))
            {
                m_welcomeMessage = requestData["login_motd"].ToString();
            }

            response.Value = responseData;
            return response;
        }
    }
}
