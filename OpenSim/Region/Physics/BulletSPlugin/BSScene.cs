﻿/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyrightD
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
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Nini.Config;
using log4net;
using OpenSim.Framework;
using OpenSim.Region.Physics.Manager;
using OpenMetaverse;
using OpenSim.Region.Framework;

// TODOs for BulletSim (for BSScene, BSPrim, BSCharacter and BulletSim)
// Adjust character capsule size when height is adjusted (ScenePresence.SetHeight)
// Test sculpties
// Compute physics FPS reasonably
// Based on material, set density and friction
// More efficient memory usage in passing hull information from BSPrim to BulletSim
// Four states of prim: Physical, regular, phantom and selected. Are we modeling these correctly?
//     In SL one can set both physical and phantom (gravity, does not effect others, makes collisions with ground)
//     At the moment, physical and phantom causes object to drop through the terrain
// Should prim.link() and prim.delink() membership checking happen at taint time?
// Mesh sharing. Use meshHash to tell if we already have a hull of that shape and only create once
// Do attachments need to be handled separately? Need collision events. Do not collide with VolumeDetect
// Implement the genCollisions feature in BulletSim::SetObjectProperties (don't pass up unneeded collisions)
// Implement LockAngularMotion
// Decide if clearing forces is the right thing to do when setting position (BulletSim::SetObjectTranslation)
// Does NeedsMeshing() really need to exclude all the different shapes?
// 
namespace OpenSim.Region.Physics.BulletSPlugin
{
public class BSScene : PhysicsScene, IPhysicsParameters
{
    private static readonly ILog m_log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);
    private static readonly string LogHeader = "[BULLETS SCENE]";

    public string BulletSimVersion = "?";

    private Dictionary<uint, BSCharacter> m_avatars = new Dictionary<uint, BSCharacter>();
    private Dictionary<uint, BSPrim> m_prims = new Dictionary<uint, BSPrim>();
    private List<BSPrim> m_vehicles = new List<BSPrim>();
    private float[] m_heightMap;
    private float m_waterLevel;
    private uint m_worldID;
    public uint WorldID { get { return m_worldID; } }

    private bool m_initialized = false;

    public IMesher mesher;
    private float m_meshLOD;
    public float MeshLOD
    {
        get { return m_meshLOD; }
    }
    private float m_sculptLOD;
    public float SculptLOD
    {
        get { return m_sculptLOD; }
    }

    private int m_maxSubSteps;
    private float m_fixedTimeStep;
    private long m_simulationStep = 0;
    public long SimulationStep { get { return m_simulationStep; } }

    // A value of the time now so all the collision and update routines do not have to get their own
    // Set to 'now' just before all the prims and actors are called for collisions and updates
    private int m_simulationNowTime;
    public int SimulationNowTime { get { return m_simulationNowTime; } }

    private int m_maxCollisionsPerFrame;
    private CollisionDesc[] m_collisionArray;
    private GCHandle m_collisionArrayPinnedHandle;

    private int m_maxUpdatesPerFrame;
    private EntityProperties[] m_updateArray;
    private GCHandle m_updateArrayPinnedHandle;

    private bool _meshSculptedPrim = true;         // cause scuplted prims to get meshed
    private bool _forceSimplePrimMeshing = false;   // if a cube or sphere, let Bullet do internal shapes

    public const uint TERRAIN_ID = 0;       // OpenSim senses terrain with a localID of zero
    public const uint GROUNDPLANE_ID = 1;

    public ConfigurationParameters Params
    {
        get { return m_params[0]; }
    }
    public Vector3 DefaultGravity
    {
        get { return new Vector3(0f, 0f, Params.gravity); }
    }

    private float m_maximumObjectMass;
    public float MaximumObjectMass
    {
        get { return m_maximumObjectMass; }
    }

    public delegate void TaintCallback();
    private List<TaintCallback> _taintedObjects;
    private Object _taintLock = new Object();

    // A pointer to an instance if this structure is passed to the C++ code
    ConfigurationParameters[] m_params;
    GCHandle m_paramsHandle;

    private BulletSimAPI.DebugLogCallback m_DebugLogCallbackHandle;

    public BSScene(string identifier)
    {
        m_initialized = false;
    }

    public override void Initialise(IMesher meshmerizer, IConfigSource config)
    {
        // Allocate pinned memory to pass parameters.
        m_params = new ConfigurationParameters[1];
        m_paramsHandle = GCHandle.Alloc(m_params, GCHandleType.Pinned);

        // Set default values for physics parameters plus any overrides from the ini file
        GetInitialParameterValues(config);

        // allocate more pinned memory close to the above in an attempt to get the memory all together
        m_collisionArray = new CollisionDesc[m_maxCollisionsPerFrame];
        m_collisionArrayPinnedHandle = GCHandle.Alloc(m_collisionArray, GCHandleType.Pinned);
        m_updateArray = new EntityProperties[m_maxUpdatesPerFrame];
        m_updateArrayPinnedHandle = GCHandle.Alloc(m_updateArray, GCHandleType.Pinned);

        // Get the version of the DLL
        // TODO: this doesn't work yet. Something wrong with marshaling the returned string.
        // BulletSimVersion = BulletSimAPI.GetVersion();
        // m_log.WarnFormat("{0}: BulletSim.dll version='{1}'", LogHeader, BulletSimVersion);

        // if Debug, enable logging from the unmanaged code
        if (m_log.IsDebugEnabled)
        {
            m_log.DebugFormat("{0}: Initialize: Setting debug callback for unmanaged code", LogHeader);
            m_DebugLogCallbackHandle = new BulletSimAPI.DebugLogCallback(BulletLogger);
            BulletSimAPI.SetDebugLogCallback(m_DebugLogCallbackHandle);
        }

        _taintedObjects = new List<TaintCallback>();

        mesher = meshmerizer;
        // The bounding box for the simulated world
        Vector3 worldExtent = new Vector3(Constants.RegionSize, Constants.RegionSize, 4096f);

        // m_log.DebugFormat("{0}: Initialize: Calling BulletSimAPI.Initialize.", LogHeader);
        m_worldID = BulletSimAPI.Initialize(worldExtent, m_paramsHandle.AddrOfPinnedObject(),
                                        m_maxCollisionsPerFrame, m_collisionArrayPinnedHandle.AddrOfPinnedObject(),
                                        m_maxUpdatesPerFrame, m_updateArrayPinnedHandle.AddrOfPinnedObject());

        m_initialized = true;
    }

    // All default parameter values are set here. There should be no values set in the
    // variable definitions.
    private void GetInitialParameterValues(IConfigSource config)
    {
        ConfigurationParameters parms = new ConfigurationParameters();

        _meshSculptedPrim = true;           // mesh sculpted prims
        _forceSimplePrimMeshing = false;    // use complex meshing if called for

        m_meshLOD = 8f;
        m_sculptLOD = 32f;

        m_maxSubSteps = 10;
        m_fixedTimeStep = 1f / 60f;
        m_maxCollisionsPerFrame = 2048;
        m_maxUpdatesPerFrame = 2048;
        m_maximumObjectMass = 10000.01f;

        parms.defaultFriction = 0.5f;
        parms.defaultDensity = 10.000006836f; // Aluminum g/cm3
        parms.defaultRestitution = 0f;
        parms.collisionMargin = 0.0f;
        parms.gravity = -9.80665f;

        parms.linearDamping = 0.0f;
        parms.angularDamping = 0.0f;
        parms.deactivationTime = 0.2f;
        parms.linearSleepingThreshold = 0.8f;
        parms.angularSleepingThreshold = 1.0f;
        parms.ccdMotionThreshold = 0.5f;    // set to zero to disable
        parms.ccdSweptSphereRadius = 0.2f;

        parms.terrainFriction = 0.5f;
        parms.terrainHitFraction = 0.8f;
        parms.terrainRestitution = 0f;
        parms.avatarFriction = 0.0f;
        parms.avatarDensity = 60f;
        parms.avatarCapsuleRadius = 0.37f;
        parms.avatarCapsuleHeight = 1.5f; // 2.140599f

        if (config != null)
        {
            // If there are specifications in the ini file, use those values
            // WHEN ADDING OR UPDATING THIS SECTION, BE SURE TO UPDATE OpenSimDefaults.ini
            // ALSO REMEMBER TO UPDATE THE RUNTIME SETTING OF THE PARAMETERS.
            IConfig pConfig = config.Configs["BulletSim"];
            if (pConfig != null)
            {
                _meshSculptedPrim = pConfig.GetBoolean("MeshSculptedPrim", _meshSculptedPrim);
                _forceSimplePrimMeshing = pConfig.GetBoolean("ForceSimplePrimMeshing", _forceSimplePrimMeshing);

                m_meshLOD = pConfig.GetFloat("MeshLevelOfDetail", m_meshLOD);
                m_sculptLOD = pConfig.GetFloat("SculptLevelOfDetail", m_sculptLOD);

                m_maxSubSteps = pConfig.GetInt("MaxSubSteps", m_maxSubSteps);
                m_fixedTimeStep = pConfig.GetFloat("FixedTimeStep", m_fixedTimeStep);
                m_maxCollisionsPerFrame = pConfig.GetInt("MaxCollisionsPerFrame", m_maxCollisionsPerFrame);
                m_maxUpdatesPerFrame = pConfig.GetInt("MaxUpdatesPerFrame", m_maxUpdatesPerFrame);
                m_maximumObjectMass = pConfig.GetFloat("MaxObjectMass", m_maximumObjectMass);

                parms.defaultFriction = pConfig.GetFloat("DefaultFriction", parms.defaultFriction);
                parms.defaultDensity = pConfig.GetFloat("DefaultDensity", parms.defaultDensity);
                parms.defaultRestitution = pConfig.GetFloat("DefaultRestitution", parms.defaultRestitution);
                parms.collisionMargin = pConfig.GetFloat("CollisionMargin", parms.collisionMargin);
                parms.gravity = pConfig.GetFloat("Gravity", parms.gravity);

                parms.linearDamping = pConfig.GetFloat("LinearDamping", parms.linearDamping);
                parms.angularDamping = pConfig.GetFloat("AngularDamping", parms.angularDamping);
                parms.deactivationTime = pConfig.GetFloat("DeactivationTime", parms.deactivationTime);
                parms.linearSleepingThreshold = pConfig.GetFloat("LinearSleepingThreshold", parms.linearSleepingThreshold);
                parms.angularSleepingThreshold = pConfig.GetFloat("AngularSleepingThreshold", parms.angularSleepingThreshold);
                parms.ccdMotionThreshold = pConfig.GetFloat("CcdMotionThreshold", parms.ccdMotionThreshold);
                parms.ccdSweptSphereRadius = pConfig.GetFloat("CcdSweptSphereRadius", parms.ccdSweptSphereRadius);

                parms.terrainFriction = pConfig.GetFloat("TerrainFriction", parms.terrainFriction);
                parms.terrainHitFraction = pConfig.GetFloat("TerrainHitFraction", parms.terrainHitFraction);
                parms.terrainRestitution = pConfig.GetFloat("TerrainRestitution", parms.terrainRestitution);
                parms.avatarFriction = pConfig.GetFloat("AvatarFriction", parms.avatarFriction);
                parms.avatarDensity = pConfig.GetFloat("AvatarDensity", parms.avatarDensity);
                parms.avatarCapsuleRadius = pConfig.GetFloat("AvatarCapsuleRadius", parms.avatarCapsuleRadius);
                parms.avatarCapsuleHeight = pConfig.GetFloat("AvatarCapsuleHeight", parms.avatarCapsuleHeight);
            }
        }
        m_params[0] = parms;
    }

    // Called directly from unmanaged code so don't do much
    private void BulletLogger(string msg)
    {
        m_log.Debug("[BULLETS UNMANAGED]:" + msg);
    }

    public override PhysicsActor AddAvatar(string avName, Vector3 position, Vector3 size, bool isFlying)
    {
        m_log.ErrorFormat("{0}: CALL TO AddAvatar in BSScene. NOT IMPLEMENTED", LogHeader);
        return null;
    }

    public override PhysicsActor AddAvatar(uint localID, string avName, Vector3 position, Vector3 size, bool isFlying)
    {
        // m_log.DebugFormat("{0}: AddAvatar: {1}", LogHeader, avName);
        BSCharacter actor = new BSCharacter(localID, avName, this, position, size, isFlying);
        lock (m_avatars) m_avatars.Add(localID, actor);
        return actor;
    }

    public override void RemoveAvatar(PhysicsActor actor)
    {
        // m_log.DebugFormat("{0}: RemoveAvatar", LogHeader);
        if (actor is BSCharacter)
        {
            ((BSCharacter)actor).Destroy();
        }
        try
        {
            lock (m_avatars) m_avatars.Remove(actor.LocalID);
        }
        catch (Exception e)
        {
            m_log.WarnFormat("{0}: Attempt to remove avatar that is not in physics scene: {1}", LogHeader, e);
        }
    }

    public override void RemovePrim(PhysicsActor prim)
    {
        // m_log.DebugFormat("{0}: RemovePrim", LogHeader);
        if (prim is BSPrim)
        {
            ((BSPrim)prim).Destroy();
        }
        try
        {
            lock (m_prims) m_prims.Remove(prim.LocalID);
        }
        catch (Exception e)
        {
            m_log.WarnFormat("{0}: Attempt to remove prim that is not in physics scene: {1}", LogHeader, e);
        }
    }

    public override PhysicsActor AddPrimShape(string primName, PrimitiveBaseShape pbs, Vector3 position,
                                              Vector3 size, Quaternion rotation, bool isPhysical, uint localID)
    {
        // m_log.DebugFormat("{0}: AddPrimShape2: {1}", LogHeader, primName);
        BSPrim prim = new BSPrim(localID, primName, this, position, size, rotation, pbs, isPhysical);
        lock (m_prims) m_prims.Add(localID, prim);
        return prim;
    }

    // This is a call from the simulator saying that some physical property has been updated.
    // The BulletSim driver senses the changing of relevant properties so this taint 
    // information call is not needed.
    public override void AddPhysicsActorTaint(PhysicsActor prim) { }

    // Simulate one timestep
    public override float Simulate(float timeStep)
    {
        int updatedEntityCount;
        IntPtr updatedEntitiesPtr;
        int collidersCount;
        IntPtr collidersPtr;

        // prevent simulation until we've been initialized
        if (!m_initialized) return 10.0f;

        // update the prim states while we know the physics engine is not busy
        ProcessTaints();

        // Some of the prims operate with special vehicle properties
        ProcessVehicles(timeStep);
        ProcessTaints();    // the vehicles might have added taints

        // step the physical world one interval
        m_simulationStep++;
        int numSubSteps = BulletSimAPI.PhysicsStep(m_worldID, timeStep, m_maxSubSteps, m_fixedTimeStep, 
                    out updatedEntityCount, out updatedEntitiesPtr, out collidersCount, out collidersPtr);

        // Don't have to use the pointers passed back since we know it is the same pinned memory we passed in

        // Get a value for 'now' so all the collision and update routines don't have to get their own
        m_simulationNowTime = Util.EnvironmentTickCount();

        // If there were collisions, process them by sending the event to the prim.
        // Collisions must be processed before updates.
        if (collidersCount > 0)
        {
            for (int ii = 0; ii < collidersCount; ii++)
            {
                uint cA = m_collisionArray[ii].aID;
                uint cB = m_collisionArray[ii].bID;
                Vector3 point = m_collisionArray[ii].point;
                Vector3 normal = m_collisionArray[ii].normal;
                SendCollision(cA, cB, point, normal, 0.01f);
                SendCollision(cB, cA, point, -normal, 0.01f);
            }
        }

        // If any of the objects had updated properties, tell the object it has been changed by the physics engine
        if (updatedEntityCount > 0)
        {
            for (int ii = 0; ii < updatedEntityCount; ii++)
            {
                EntityProperties entprop = m_updateArray[ii];
                // m_log.DebugFormat("{0}: entprop[{1}]: id={2}, pos={3}", LogHeader, ii, entprop.ID, entprop.Position);
                BSCharacter actor;
                if (m_avatars.TryGetValue(entprop.ID, out actor))
                {
                    actor.UpdateProperties(entprop);
                    continue;
                }
                BSPrim prim;
                if (m_prims.TryGetValue(entprop.ID, out prim))
                {
                    prim.UpdateProperties(entprop);
                }
            }
        }

        // TODO: FIX THIS: fps calculation wrong. This calculation always returns about 1 in normal operation.
        return timeStep / (numSubSteps * m_fixedTimeStep) * 1000f;
    }

    // Something has collided
    private void SendCollision(uint localID, uint collidingWith, Vector3 collidePoint, Vector3 collideNormal, float penitration)
    {
        if (localID == TERRAIN_ID || localID == GROUNDPLANE_ID)
        {
            return;         // don't send collisions to the terrain
        }

        ActorTypes type = ActorTypes.Prim;
        if (collidingWith == TERRAIN_ID || collidingWith == GROUNDPLANE_ID)
            type = ActorTypes.Ground;
        else if (m_avatars.ContainsKey(collidingWith))
            type = ActorTypes.Agent;

        BSPrim prim;
        if (m_prims.TryGetValue(localID, out prim)) {
            prim.Collide(collidingWith, type, collidePoint, collideNormal, penitration);
            return;
        }
        BSCharacter actor;
        if (m_avatars.TryGetValue(localID, out actor)) {
            actor.Collide(collidingWith, type, collidePoint, collideNormal, penitration);
            return;
        }
        return;
    }

    public override void GetResults() { }

    public override void SetTerrain(float[] heightMap) {
        m_heightMap = heightMap;
        this.TaintedObject(delegate()
        {
            BulletSimAPI.SetHeightmap(m_worldID, m_heightMap);
        });
    }

    public float GetTerrainHeightAtXY(float tX, float tY)
    {
        return m_heightMap[((int)tX) * Constants.RegionSize + ((int)tY)];
    }

    public override void SetWaterLevel(float baseheight) 
    {
        m_waterLevel = baseheight;
    }
    public float GetWaterLevel()
    {
        return m_waterLevel;
    }

    public override void DeleteTerrain() 
    {
        m_log.DebugFormat("{0}: DeleteTerrain()", LogHeader);
    }

    public override void Dispose()
    {
        m_log.DebugFormat("{0}: Dispose()", LogHeader);
    }

    public override Dictionary<uint, float> GetTopColliders()
    {
        return new Dictionary<uint, float>();
    }

    public override bool IsThreaded { get { return false;  } }

    /// <summary>
    /// Routine to figure out if we need to mesh this prim with our mesher
    /// </summary>
    /// <param name="pbs"></param>
    /// <returns>true if the prim needs meshing</returns>
    public bool NeedsMeshing(PrimitiveBaseShape pbs)
    {
        // most of this is redundant now as the mesher will return null if it cant mesh a prim
        // but we still need to check for sculptie meshing being enabled so this is the most
        // convenient place to do it for now...

        // int iPropertiesNotSupportedDefault = 0;

        if (pbs.SculptEntry && !_meshSculptedPrim)
        {
            // Render sculpties as boxes
            return false;
        }

        // if it's a standard box or sphere with no cuts, hollows, twist or top shear, return false since Bullet 
        // can use an internal representation for the prim
        if (!_forceSimplePrimMeshing)
        {
            if ((pbs.ProfileShape == ProfileShape.Square && pbs.PathCurve == (byte)Extrusion.Straight)
                || (pbs.ProfileShape == ProfileShape.HalfCircle && pbs.PathCurve == (byte)Extrusion.Curve1
                        && pbs.Scale.X == pbs.Scale.Y && pbs.Scale.Y == pbs.Scale.Z))
            {

                if (pbs.ProfileBegin == 0 && pbs.ProfileEnd == 0
                    && pbs.ProfileHollow == 0
                    && pbs.PathTwist == 0 && pbs.PathTwistBegin == 0
                    && pbs.PathBegin == 0 && pbs.PathEnd == 0
                    && pbs.PathTaperX == 0 && pbs.PathTaperY == 0
                    && pbs.PathScaleX == 100 && pbs.PathScaleY == 100
                    && pbs.PathShearX == 0 && pbs.PathShearY == 0)
                {
                    return false;
                }
            }
        }

        /*  TODO: verify that the mesher will now do all these shapes
        if (pbs.ProfileHollow != 0)
            iPropertiesNotSupportedDefault++;

        if ((pbs.PathBegin != 0) || pbs.PathEnd != 0)
            iPropertiesNotSupportedDefault++;

        if ((pbs.PathTwistBegin != 0) || (pbs.PathTwist != 0))
            iPropertiesNotSupportedDefault++; 

        if ((pbs.ProfileBegin != 0) || pbs.ProfileEnd != 0)
            iPropertiesNotSupportedDefault++;

        if ((pbs.PathScaleX != 100) || (pbs.PathScaleY != 100))
            iPropertiesNotSupportedDefault++;

        if ((pbs.PathShearX != 0) || (pbs.PathShearY != 0))
            iPropertiesNotSupportedDefault++;

        if (pbs.ProfileShape == ProfileShape.Circle && pbs.PathCurve == (byte)Extrusion.Straight)
            iPropertiesNotSupportedDefault++;

        if (pbs.ProfileShape == ProfileShape.HalfCircle && pbs.PathCurve == (byte)Extrusion.Curve1 && (pbs.Scale.X != pbs.Scale.Y || pbs.Scale.Y != pbs.Scale.Z || pbs.Scale.Z != pbs.Scale.X))
            iPropertiesNotSupportedDefault++;

        if (pbs.ProfileShape == ProfileShape.HalfCircle && pbs.PathCurve == (byte) Extrusion.Curve1)
            iPropertiesNotSupportedDefault++;

        // test for torus
        if ((pbs.ProfileCurve & 0x07) == (byte)ProfileShape.Square)
        {
            if (pbs.PathCurve == (byte)Extrusion.Curve1)
            {
                iPropertiesNotSupportedDefault++;
            }
        }
        else if ((pbs.ProfileCurve & 0x07) == (byte)ProfileShape.Circle)
        {
            if (pbs.PathCurve == (byte)Extrusion.Straight)
            {
                iPropertiesNotSupportedDefault++;
            }
            // ProfileCurve seems to combine hole shape and profile curve so we need to only compare against the lower 3 bits
            else if (pbs.PathCurve == (byte)Extrusion.Curve1)
            {
                iPropertiesNotSupportedDefault++;
            }
        }
        else if ((pbs.ProfileCurve & 0x07) == (byte)ProfileShape.HalfCircle)
        {
            if (pbs.PathCurve == (byte)Extrusion.Curve1 || pbs.PathCurve == (byte)Extrusion.Curve2)
            {
                iPropertiesNotSupportedDefault++;
            }
        }
        else if ((pbs.ProfileCurve & 0x07) == (byte)ProfileShape.EquilateralTriangle)
        {
            if (pbs.PathCurve == (byte)Extrusion.Straight)
            {
                iPropertiesNotSupportedDefault++;
            }
            else if (pbs.PathCurve == (byte)Extrusion.Curve1)
            {
                iPropertiesNotSupportedDefault++;
            }
        }
        if (iPropertiesNotSupportedDefault == 0)
        {
            return false;
        }
         */
        return true; 
    }

    // The calls to the PhysicsActors can't directly call into the physics engine
    // because it might be busy. We we delay changes to a known time.
    // We rely on C#'s closure to save and restore the context for the delegate.
    public void TaintedObject(TaintCallback callback)
    {
        lock (_taintLock)
            _taintedObjects.Add(callback);
        return;
    }

    // When someone tries to change a property on a BSPrim or BSCharacter, the object queues
    // a callback into itself to do the actual property change. That callback is called
    // here just before the physics engine is called to step the simulation.
    public void ProcessTaints()
    {
        if (_taintedObjects.Count > 0)  // save allocating new list if there is nothing to process
        {
            // swizzle a new list into the list location so we can process what's there
            List<TaintCallback> oldList;
            lock (_taintLock)
            {
                oldList = _taintedObjects;
                _taintedObjects = new List<TaintCallback>();
            }

            foreach (TaintCallback callback in oldList)
            {
                try
                {
                    callback();
                }
                catch (Exception e)
                {
                    m_log.ErrorFormat("{0}: ProcessTaints: Exception: {1}", LogHeader, e);
                }
            }
            oldList.Clear();
        }
    }

    #region Vehicles
    // Make so the scene will call this prim for vehicle actions each tick.
    // Safe to call if prim is already in the vehicle list.
    public void AddVehiclePrim(BSPrim vehicle)
    {
        lock (m_vehicles)
        {
            if (!m_vehicles.Contains(vehicle))
            {
                m_vehicles.Add(vehicle);
            }
        }
    }

    // Remove a prim from our list of vehicles.
    // Safe to call if the prim is not in the vehicle list.
    public void RemoveVehiclePrim(BSPrim vehicle)
    {
        lock (m_vehicles)
        {
            if (m_vehicles.Contains(vehicle))
            {
                m_vehicles.Remove(vehicle);
            }
        }
    }

    // Some prims have extra vehicle actions
    // no locking because only called when physics engine is not busy
    private void ProcessVehicles(float timeStep)
    {
        foreach (BSPrim prim in m_vehicles)
        {
            prim.StepVehicle(timeStep);
        }
    }
    #endregion Vehicles

    #region Runtime settable parameters
    public static PhysParameterEntry[] SettableParameters = new PhysParameterEntry[]
    {
        new PhysParameterEntry("MeshLOD", "Level of detail to render meshes (32, 16, 8 or 4. 32=most detailed)"),
        new PhysParameterEntry("SculptLOD", "Level of detail to render sculpties (32, 16, 8 or 4. 32=most detailed)"),
        new PhysParameterEntry("MaxSubStep", "In simulation step, maximum number of substeps"),
        new PhysParameterEntry("FixedTimeStep", "In simulation step, seconds of one substep (1/60)"),
        new PhysParameterEntry("MaxObjectMass", "Maximum object mass (10000.01)"),

        new PhysParameterEntry("DefaultFriction", "Friction factor used on new objects"),
        new PhysParameterEntry("DefaultDensity", "Density for new objects" ),
        new PhysParameterEntry("DefaultRestitution", "Bouncyness of an object" ),
        // new PhysParameterEntry("CollisionMargin", "Margin around objects before collisions are calculated (must be zero!!)" ),
        new PhysParameterEntry("Gravity", "Vertical force of gravity (negative means down)" ),

        new PhysParameterEntry("LinearDamping", "Factor to damp linear movement per second (0.0 - 1.0)" ),
        new PhysParameterEntry("AngularDamping", "Factor to damp angular movement per second (0.0 - 1.0)" ),
        new PhysParameterEntry("DeactivationTime", "Seconds before considering an object potentially static" ),
        new PhysParameterEntry("LinearSleepingThreshold", "Seconds to measure linear movement before considering static" ),
        new PhysParameterEntry("AngularSleepingThreshold", "Seconds to measure angular movement before considering static" ),
        // new PhysParameterEntry("CcdMotionThreshold", "" ),
        // new PhysParameterEntry("CcdSweptSphereRadius", "" ),

        new PhysParameterEntry("TerrainFriction", "Factor to reduce movement against terrain surface" ),
        new PhysParameterEntry("TerrainHitFraction", "Distance to measure hit collisions" ),
        new PhysParameterEntry("TerrainRestitution", "Bouncyness" ),
        new PhysParameterEntry("AvatarFriction", "Factor to reduce movement against an avatar. Changed on avatar recreation." ),
        new PhysParameterEntry("AvatarDensity", "Density of an avatar. Changed on avatar recreation." ),
        new PhysParameterEntry("AvatarRestitution", "Bouncyness. Changed on avatar recreation." ),
        new PhysParameterEntry("AvatarCapsuleRadius", "Radius of space around an avatar" ),
        new PhysParameterEntry("AvatarCapsuleHeight", "Default height of space around avatar" )
    };

    #region IPhysicsParameters
    // Get the list of parameters this physics engine supports
    public PhysParameterEntry[] GetParameterList()
    {
        return SettableParameters;
    }

    // Set parameter on a specific or all instances.
    // Return 'false' if not able to set the parameter.
    // Setting the value in the m_params block will change the value the physics engine
    //   will use the next time since it's pinned and shared memory.
    // Some of the values require calling into the physics engine to get the new
    //   value activated ('terrainFriction' for instance).
    public bool SetPhysicsParameter(string parm, float val, uint localID)
    {
        bool ret = true;
        string lparm = parm.ToLower();
        switch (lparm)
        {
            case "meshlod": m_meshLOD = (int)val; break;
            case "sculptlod": m_sculptLOD = (int)val; break;
            case "maxsubstep": m_maxSubSteps = (int)val; break;
            case "fixedtimestep": m_fixedTimeStep = val; break;
            case "maxobjectmass": m_maximumObjectMass = val; break;

            case "defaultfriction": m_params[0].defaultFriction = val; break;
            case "defaultdensity": m_params[0].defaultDensity = val; break;
            case "defaultrestitution": m_params[0].defaultRestitution = val; break;
            case "collisionmargin": m_params[0].collisionMargin = val; break;
            case "gravity": m_params[0].gravity = val;  TaintedUpdateParameter(lparm, PhysParameterEntry.APPLY_TO_NONE, val); break;

            case "lineardamping": UpdateParameterPrims(ref m_params[0].linearDamping, lparm, localID, val); break;
            case "angulardamping": UpdateParameterPrims(ref m_params[0].angularDamping, lparm, localID, val); break;
            case "deactivationtime": UpdateParameterPrims(ref m_params[0].deactivationTime, lparm, localID, val); break;
            case "linearsleepingthreshold": UpdateParameterPrims(ref m_params[0].linearSleepingThreshold, lparm, localID, val); break;
            case "angularsleepingthreshold": UpdateParameterPrims(ref m_params[0].angularDamping, lparm, localID, val); break;
            case "ccdmotionthreshold": UpdateParameterPrims(ref m_params[0].ccdMotionThreshold, lparm, localID, val); break;
            case "ccdsweptsphereradius": UpdateParameterPrims(ref m_params[0].ccdSweptSphereRadius, lparm, localID, val); break;

            // set a terrain physical feature and cause terrain to be recalculated
            case "terrainfriction": m_params[0].terrainFriction = val; TaintedUpdateParameter("terrain", 0, val); break;
            case "terrainhitfraction": m_params[0].terrainHitFraction = val; TaintedUpdateParameter("terrain", 0, val); break;
            case "terrainrestitution": m_params[0].terrainRestitution = val; TaintedUpdateParameter("terrain", 0, val); break;
            // set an avatar physical feature and cause avatar(s) to be recalculated
            case "avatarfriction": UpdateParameterAvatars(ref m_params[0].avatarFriction, "avatar", localID, val); break;
            case "avatardensity": UpdateParameterAvatars(ref m_params[0].avatarDensity, "avatar", localID, val);  break;
            case "avatarrestitution": UpdateParameterAvatars(ref m_params[0].avatarRestitution, "avatar", localID, val); break;
            case "avatarcapsuleradius": UpdateParameterAvatars(ref m_params[0].avatarCapsuleRadius, "avatar", localID, val);  break;
            case "avatarcapsuleheight": UpdateParameterAvatars(ref m_params[0].avatarCapsuleHeight, "avatar", localID, val);  break;

            default: ret = false; break;
        }
        return ret;
    }

    // check to see if we are updating a parameter for a particular or all of the prims
    private void UpdateParameterPrims(ref float loc, string parm, uint localID, float val)
    {
        List<uint> operateOn;
        lock (m_prims) operateOn = new List<uint>(m_prims.Keys);
        UpdateParameterSet(operateOn, ref loc, parm, localID, val);
    }

    // check to see if we are updating a parameter for a particular or all of the avatars
    private void UpdateParameterAvatars(ref float loc, string parm, uint localID, float val)
    {
        List<uint> operateOn;
        lock (m_avatars) operateOn = new List<uint>(m_avatars.Keys);
        UpdateParameterSet(operateOn, ref loc, parm, localID, val);
    }

    // update all the localIDs specified
    // If the local ID is APPLY_TO_NONE, just change the default value
    // If the localID is APPLY_TO_ALL change the default value and apply the new value to all the lIDs
    // If the localID is a specific object, apply the parameter change to only that object
    private void UpdateParameterSet(List<uint> lIDs, ref float defaultLoc, string parm, uint localID, float val)
    {
        switch (localID)
        {
            case PhysParameterEntry.APPLY_TO_NONE:
                defaultLoc = val;   // setting only the default value
                break;
            case PhysParameterEntry.APPLY_TO_ALL:
                defaultLoc = val;  // setting ALL also sets the default value
                List<uint> objectIDs = lIDs;
                string xparm = parm.ToLower();
                float xval = val;
                TaintedObject(delegate() {
                    foreach (uint lID in objectIDs)
                    {
                        BulletSimAPI.UpdateParameter(m_worldID, lID, xparm, xval);
                    }
                });
                break;
            default: 
                // setting only one localID
                TaintedUpdateParameter(parm, localID, val);
                break;
        }
    }

    // schedule the actual updating of the paramter to when the phys engine is not busy
    private void TaintedUpdateParameter(string parm, uint localID, float val)
    {
        uint xlocalID = localID;
        string xparm = parm.ToLower();
        float xval = val;
        TaintedObject(delegate() {
            BulletSimAPI.UpdateParameter(m_worldID, xlocalID, xparm, xval);
        });
    }

    // Get parameter.
    // Return 'false' if not able to get the parameter.
    public bool GetPhysicsParameter(string parm, out float value)
    {
        float val = 0f;
        bool ret = true;
        switch (parm.ToLower())
        {
            case "meshlod": val = (float)m_meshLOD; break;
            case "sculptlod": val = (float)m_sculptLOD; break;
            case "maxsubstep": val = (float)m_maxSubSteps; break;
            case "fixedtimestep": val = m_fixedTimeStep; break;
            case "maxobjectmass": val = m_maximumObjectMass; break;

            case "defaultfriction": val = m_params[0].defaultFriction; break;
            case "defaultdensity": val = m_params[0].defaultDensity; break;
            case "defaultrestitution": val = m_params[0].defaultRestitution; break;
            case "collisionmargin": val = m_params[0].collisionMargin; break;
            case "gravity": val = m_params[0].gravity; break;

            case "lineardamping": val = m_params[0].linearDamping; break;
            case "angulardamping": val = m_params[0].angularDamping; break;
            case "deactivationtime": val = m_params[0].deactivationTime; break;
            case "linearsleepingthreshold": val = m_params[0].linearSleepingThreshold; break;
            case "angularsleepingthreshold": val = m_params[0].angularDamping; break;
            case "ccdmotionthreshold": val = m_params[0].ccdMotionThreshold; break;
            case "ccdsweptsphereradius": val = m_params[0].ccdSweptSphereRadius; break;

            case "terrainfriction": val = m_params[0].terrainFriction; break;
            case "terrainhitfraction": val = m_params[0].terrainHitFraction; break;
            case "terrainrestitution": val = m_params[0].terrainRestitution; break;

            case "avatarfriction": val = m_params[0].avatarFriction; break;
            case "avatardensity": val = m_params[0].avatarDensity; break;
            case "avatarrestitution": val = m_params[0].avatarRestitution; break;
            case "avatarcapsuleradius": val = m_params[0].avatarCapsuleRadius; break;
            case "avatarcapsuleheight": val = m_params[0].avatarCapsuleHeight; break;
            default: ret = false; break;

        }
        value = val;
        return ret;
    }

    #endregion IPhysicsParameters

    #endregion Runtime settable parameters

}
}
