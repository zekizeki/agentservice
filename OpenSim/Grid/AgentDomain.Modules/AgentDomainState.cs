/*m
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
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Reflection;
using log4net;
using log4net.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Framework.Console;
using OpenSim.Framework.Servers;

using OpenSim.Grid.AgentDomain.Modules;
// Structured Data
using OpenMetaverse.StructuredData;
using OSD = OpenMetaverse.StructuredData.OSD;
using OSDMap = OpenMetaverse.StructuredData.OSDMap;
using OSDArray = OpenMetaverse.StructuredData.OSDArray;



namespace OpenSim.Grid.AgentDomain.Modules
{
public enum AgentADState 
	{
	logged_out=1,
	authenticated = 2 ,
	logged_in = 3,
	rezzed = 4, 
	unknown = 5
	}



public class NameUUIDEntry
	{
	public struct UUID_name_pair
	   {
	   public string firstName;
	   public string lastName;
	   public UUID agentUUID;
	   public string password;
          }
       
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);
        public Dictionary<string,UUID_name_pair> nameUUIDTable;
        public Dictionary<string,UUID> capToUUIDTable;

	public NameUUIDEntry()
	{
	m_log.Info("[Agent Domain]: Initing UUID/Name tablee for AD");
        nameUUIDTable = new Dictionary<string,UUID_name_pair>();
        capToUUIDTable = new Dictionary<string,UUID>();
	}

	
	public void linkCap(string cap, UUID agentUUID)
	{
	capToUUIDTable.Add(cap,agentUUID);
	}

	public void breakCapLink(UUID agentUUID)
	{	
	string theKey = "";				   
        foreach (KeyValuePair<string, UUID> kvp in capToUUIDTable)
	   {
           if (kvp.Value == agentUUID)
	       {
	       theKey = kvp.Key;
	       break;
	       }
	   } // End foreach
	 if ("" == theKey)
	    {
	    m_log.InfoFormat("[Agent domain]: attempt to delete nonexistant agent with UUID {0} from caps table.");
	    }
	    else
	    {
            capToUUIDTable.Remove(theKey);
	    }
	}


	public string getCapForUUID(UUID agentUUID)
	{
	string theKey = "";				   
        foreach (KeyValuePair<string, UUID> kvp in capToUUIDTable)
	   {
           if (kvp.Value == agentUUID)
	       {
	       theKey = kvp.Key;
	       break;
	       }
	   } // End foreach
	return theKey;
	}

	public UUID getAgentUUIDforCap(string cap)
	
	{
	UUID capUUID;
	if ( capToUUIDTable.TryGetValue(cap, out capUUID) )
	   {
           return capUUID;
           }
           else 
	   {
	   return UUID.Zero;
	   }
        }

        
	
        // This breaks out into a seperate class soon

      	public UUID lookupAgentUUIDByName(string firstName, string lastName,UserDataBaseService userDB)
	{
	    m_log.InfoFormat("[Agent domain]: lookupAgentUUIDByName {0} {1}",firstName,lastName);
	    string agentKey = firstName+"."+lastName;
        
            UUID_name_pair workEntry;

            // Rob lookup user in the user table
            UserProfileData userData= null;
            if (userDB != null)
               {
               userData= userDB.GetUserProfile(firstName, lastName);
	       if (null != userData)
	          {
                  workEntry.agentUUID = userData.ID;//UUID.Random();	
	          }
	          else 
	          {
	          m_log.InfoFormat("[AGENT DOMAIN]: null user data when looking up {0} {1}",firstName,lastName);
  	          workEntry.agentUUID = UUID.Random();
                  }
	       }
	       else 
	         {
	         m_log.InfoFormat("[AGENT DOMAIN]: null userDB when looking up {0} {1}",firstName,lastName);
  	         workEntry.agentUUID = UUID.Random();
                 }
m_log.Info("got uuid its " + userData.ID);
       workEntry.firstName = firstName;
       workEntry.lastName = lastName;
       workEntry.password = "*";
       m_log.Info("here2");
       m_log.InfoFormat("[Agent domain]: found UUID for agent {0} {1} UUID is {2}",firstName,lastName,workEntry.agentUUID);
       return workEntry.agentUUID;
	}

 
    // Register an entry to the table

    public void registerUser(string fn, string ln, string uuid, string pw)
	{
	m_log.InfoFormat("[AGENT DOMAIN]: Adding>{0}<>{1}< to registry with UUID {2}",fn,ln, uuid);
	string agentKey = fn+"."+ln;
	if (nameUUIDTable.ContainsKey(agentKey) )
           {
	   m_log.InfoFormat("[Agent Domain]:  agent for {0} {1} is already in table.",fn,ln);
	   return;
           }
        UUID_name_pair newEntry = new UUID_name_pair();
            newEntry.firstName = fn;
 	        newEntry.lastName = ln;
            newEntry.agentUUID = new UUID(uuid);
	        newEntry.password = pw;
	        nameUUIDTable.Add(agentKey,newEntry);
	}



   }   // end of class NameUUIDEntery 

//------------------------------------------------------------------
//
// A small class for the row in an agent state table 
//
//-------------------------------------------------------------------
 
public class Agent_state_entry
	   {
   	  

	   public string firstName
	   {
	   get {return i_firstName;}
	   set {i_firstName = value;}
           }
   	   public string lastName
	   {
	   get {return i_lastName;}
	   set {i_lastName = value;}
           }
	   public AgentADState agentState
	   {
	   get {return (AgentADState) i_agentState;}
	   set {i_agentState = (int) value;}
           }
	   public string currentLocation
	   {
	   get {return i_firstName;}
	   set {i_firstName = value;}
           }
	   public string derezCap
	   {
	   get {return i_derezCap;}
	   set {i_derezCap = value;}
           }
	   public Int32 circuitCode
	   {
	   get {return i_circuitCode;}
	   set {i_circuitCode = value;}
           }
	   public UUID secureSessionID
	   {
	   get {return i_secureSessionID;}
	   set {i_secureSessionID = value;}
           }

	   protected string i_lastName;
	   protected int i_agentState;
	   protected string i_currentLocation;
	   protected string i_derezCap;
	   protected Int32 i_circuitCode;
	   protected UUID i_secureSessionID;
	   protected string i_firstName;

	  public Agent_state_entry()
	    {
	    Console.WriteLine("ASE constructor called");
	    i_firstName = "DEAD";
	    i_lastName = "BEEF";
	    i_agentState = (int)AgentADState.unknown;
	    i_currentLocation = "";
	    i_derezCap = "";
	    i_circuitCode = 0;
	    i_secureSessionID = UUID.Zero;
	    }

	   public Agent_state_entry(Agent_state_entry input)
           {
	    Console.WriteLine("ASE copy constructor called");
	    i_firstName = input.i_firstName;
	    i_lastName = input.i_lastName;
	    i_agentState = input.i_agentState;
	    i_currentLocation = input.i_currentLocation;
	    i_derezCap = input.i_derezCap;
	    i_circuitCode = input.i_circuitCode;
	    i_secureSessionID = input.i_secureSessionID;
           }
	
           }

public class ActiveAgentState
	{

        
	public Dictionary<UUID,Agent_state_entry> agentStateTable;
	public object agentStateTableLock;
        private static readonly ILog m_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

	// Null constructor

	public ActiveAgentState()
	{
	m_log.Info("[Agent Domain]: Initing Agent State Table for AD");
	agentStateTable = new Dictionary <UUID,Agent_state_entry>();
	agentStateTableLock = new object();
	}

	// Lookup state info if there is any 

	public Agent_state_entry lookupAgentStateByUUID(UUID agentUUID)
	       {
	       m_log.InfoFormat("Looking up state for UUID {0}",agentUUID.ToString());
	       Agent_state_entry dictEntry = new Agent_state_entry();
	       dictEntry.firstName = "Live";
	       m_log.InfoFormat("A) Looking up state for UUID {0}",agentUUID.ToString());
               m_log.InfoFormat("A1) name was{0} {1}",dictEntry.firstName,dictEntry.lastName);
	       if (null == agentStateTable)
	          {
	          m_log.Info("GAH!");
	          }
	          else
                  {
		  m_log.InfoFormat("F) got {0}",agentStateTable);
		  }
	       bool foundIt = agentStateTable.TryGetValue(agentUUID, out dictEntry);
	       m_log.Info("HAH!");
               if ( foundIt )
                  {
                  m_log.Info("B) Found state for UUID {0}");
	          return dictEntry;
                  }
                  else
                  {
                  m_log.Info("C) No entry {1}");
		  Agent_state_entry foo = new Agent_state_entry();
		  m_log.InfoFormat("C1) Name is {0} {1}",foo.firstName,foo.lastName);
		  foo.agentState = AgentADState.unknown;
	          return foo;
                  }
	}

	// ---------------------------------------------
	//
	// Update the state of an agent in the active 
	// state table
	//
	// ---------------------------------------------

	public void updateAgentState(UUID agentUUID, AgentADState status)
	{
	Agent_state_entry newState = new Agent_state_entry();
	Agent_state_entry oldState;
	//lock(agentStateTableLock)
        {
	if (agentStateTable.TryGetValue(agentUUID, out oldState) )
	   {
	   newState.firstName = oldState.firstName;
	   newState.lastName = oldState.lastName; 
	   newState.agentState = status;
	   newState.derezCap = oldState.derezCap;
	   newState.secureSessionID = oldState.secureSessionID;
	   newState.circuitCode = oldState.circuitCode;
	   agentStateTable.Remove(agentUUID);
	   agentStateTable.Add(agentUUID,newState);
	   }
	   else
	   {
	   m_log.WarnFormat("[Agent domain]: Major error trying to update state of agent with UUID {0}",agentUUID);
           }
	} // end lock
	return;
        } // end method


	//-----------------------------------
	//
	// Update state with caps and goodies
        //
	//------------------------------------

	public void updateAgentStateAndCapAndStuff(UUID agentUUID, AgentADState status, string deRezCap, UUID secSessId, Int32 circCode )
	{
	Agent_state_entry newState = new Agent_state_entry();
	Agent_state_entry oldState;
	lock(agentStateTableLock);
        {
	if (agentStateTable.TryGetValue(agentUUID, out oldState) )
	   {
	   newState.firstName = oldState.firstName;
	   newState.lastName = oldState.lastName; 
	   newState.agentState = status;
	   newState.derezCap = deRezCap;
	   newState.secureSessionID = secSessId;
	   newState.circuitCode = circCode;
	   agentStateTable.Remove(agentUUID);
	   agentStateTable.Add(agentUUID,newState);
	   }
	   else
	   {
	   m_log.WarnFormat("[Agent domain]: Major error trying to update state of agent with UUID {0}",agentUUID);
           }
	} // end lock
	return;
        } // end method


	// ---------------------------
	// Create a record for a user
	// ---------------------------

	public Agent_state_entry createAgentEntryByName(string firstName, string lastName,UUID agentUUID)
	       {
	       Agent_state_entry returnedState = new Agent_state_entry();
	       returnedState.agentState = AgentADState.logged_in;
	       returnedState.firstName = firstName;
	       returnedState.lastName = lastName;
	       Agent_state_entry dictState = new Agent_state_entry();
	       lock(agentStateTableLock);
                   {
              	   if (agentStateTable.TryGetValue(agentUUID,out dictState) )
                      {
	              returnedState = dictState;
                      }
	           agentStateTable.Add(agentUUID,returnedState);
	       	   } // End lock 
	       return returnedState;

	       } // end method 


	public string returnADStateString()
	{
	string returnString = "Agent Domain Table/n";
	                     
        foreach (KeyValuePair<UUID,Agent_state_entry> kvp1 in agentStateTable)
	   {
	   string lineString = "";
	   UUID agentUUID = kvp1.Key;
           Agent_state_entry lineEntry = kvp1.Value;
	   lineString = lineEntry.firstName + " " + lineEntry.lastName + "( " + agentUUID.ToString() + ")" ;
	   switch (lineEntry.agentState)
	          {
		 case AgentADState.logged_in:
	             lineString = lineString + "logged in at ";
	             lineString = lineString + lineEntry.derezCap + " ";
		      break;
                 case AgentADState.logged_out: 	          
                   lineString = lineString + "logged out";
                   break;
                 case AgentADState.unknown: 
	           lineString = lineString + "unknown";
	           break;
	         default:
	           lineString = lineString + "DEFAULT";
	           break;
		}
	returnString = returnString + lineString + "/n";
	}
	return returnString;
	}



 } // end of ActiveAgentState Class
	       

}
