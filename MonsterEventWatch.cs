using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using GorillaNetworking;
using Photon.Pun;
using Photon.Realtime;
using Hash = ExitGames.Client.Photon.Hashtable;

[BepInPlugin("local.monsterevent.watch", "Monster Event Watch", "0.3.3")]
public class MonsterEventWatch : BaseUnityPlugin
{
    const string EventPath="City_Pretty/Event_Overview_AlienFight";
    const string ExperiencePath=EventPath+"/10_16_AlienFightExperience";
    const string VersionKey="MEW.Version", ReadyKey="MEW.Ready", RequestKey="MEW.Request", SessionKey="MEW.Session", RunKey="MEW.Run";
    static MonsterEventWatch self;
    Harmony patches;
    GameObject watch;
    Transform button;
    TextMesh text;
    bool touching, replay, playing, stopping, shared;
    float nextPress, nextSync, nextCoreCheck, beganAt;
    string lastStatus, replayRoom, lastRoom, seenSession, sentRequest, activeSession;
    readonly Dictionary<int,string> requests=new Dictionary<int,string>();
    Coroutine routine;
    GorillaEventAnimationController controller;
    TeleportMarker cityMarker;
    EntitySpawner[] spawners=Array.Empty<EntitySpawner>();
    EntitySpawnManager[] coreManagers=Array.Empty<EntitySpawnManager>();
    GameEntityManager offlineManager;
    object offlineZoneState;
    bool coresInitialized;
    bool keyboardAvailable=true;
    readonly List<TimerSnapshot> timers=new List<TimerSnapshot>();
    static bool InRoom => PhotonNetwork.InRoom && PhotonNetwork.CurrentRoom!=null;
    static bool PrivateRoom => InRoom && !PhotonNetwork.CurrentRoom.IsVisible;
    static bool Offline => NetworkSystem.Instance!=null && !NetworkSystem.Instance.InRoom && !InRoom;
    bool ContextValid => shared ? PrivateRoom && PhotonNetwork.CurrentRoom.Name==replayRoom : Offline;
    static FieldInfo Field(Type t,string n) => AccessTools.Field(t,n) ?? throw new MissingFieldException(t.FullName,n);
    static object Get(object obj,string n) => Field(obj.GetType(),n).GetValue(obj);
    static void Set(object obj,string n,object value) => Field(obj.GetType(),n).SetValue(obj,value);
    // A few event effects are flattened out of their original hierarchy by the game.
    static string PathOf(Transform t)
    {
        var parts=new List<string>(); var visited=new HashSet<Transform>();
        while(t!=null && visited.Add(t)) {
            parts.Add(t.name); var parent=t.parent;
            var flattener=t.GetComponent<ObjectHierarchyFlattener>();
            if(flattener!=null && (bool)Get(flattener,"initialized")) parent=Get(flattener,"originalParentTransform") as Transform ?? parent;
            t=parent;
        }
        parts.Reverse(); return string.Join("/",parts);
    }
    static bool Loaded(Component c) => c!=null && c.gameObject.scene.IsValid() && c.gameObject.scene.isLoaded;
    static bool EventObject(Component c) => Loaded(c) && PathOf(c.transform).StartsWith(EventPath+"/",StringComparison.Ordinal);
    static T Find<T>(string path) where T:Component => Resources.FindObjectsOfTypeAll<T>().FirstOrDefault(x=>Loaded(x) && PathOf(x.transform)==path);
    static bool Target(TimedUnityEventDispatcher timer) => Loaded(timer) && timer.gameObject.scene.name=="City" && (PathOf(timer.transform)==EventPath+"/Cityside/TimedUnityEventDispatcher" || PathOf(timer.transform)=="City_Pretty/10_18_Vista_Destroyed") && (string)Get(timer,"titleDataKey")=="Event_Alien";
    void Awake() { self=this; patches=new Harmony("local.monsterevent.watch"); patches.PatchAll(typeof(MonsterEventWatch).Assembly); Logger.LogInfo("v0.3.3 Meta PCVR: reset leftover ship-parent rotation and cockpit stabilizer before replay"); }
    void Update()
    {
        try { Tick(); } catch(Exception ex) { Logger.LogError(ex); if(replay) StopReplay(false,"ERROR — SEE LOG"); }
    }
    void Tick()
    {
        if(replay && !ContextValid) StopReplay(false,"ROOM CHANGED");
        var tagger=GorillaTagger.Instance;
        if(tagger==null || tagger.leftHandTransform==null || tagger.rightHandTransform==null) return;
        if(watch==null) MakeWatch(tagger.leftHandTransform);
        if(Time.unscaledTime>=nextSync) { nextSync=Time.unscaledTime+.3f; SyncTick(); }
        if(replay && controller==null) StopReplay(false,"SCENE UNLOADED");
        if(replay && playing) {
            InitializeCores();
            // Completion follows the actual animation, not an estimated duration timeout.
            if(Time.unscaledTime>beganAt+2 && !(bool)Get(controller,"playAnimation")) StopReplay(true,"FINISHED");
            if(CancelPressed()) StopReplay(true,"CANCELLED LOCALLY");
        }
        bool visible=!replay || !playing;
        if(watch.activeSelf!=visible) watch.SetActive(visible);
        // Hidden controls cannot cancel the event through hand collisions.
        if(replay) { touching=false; return; }
        float scale=Mathf.Abs(tagger.leftHandTransform.lossyScale.x);
        bool hit=Vector3.Distance(tagger.rightHandTransform.position,button.position)<.065f*scale;
        if(hit && !touching && Time.unscaledTime>nextPress) { nextPress=Time.unscaledTime+1.5f; Press(); }
        touching=hit;
    }
    bool CancelPressed()
    {
        if(!keyboardAvailable) return false;
        try {
            var keyboard=UnityEngine.InputSystem.Keyboard.current;
            return keyboard!=null && keyboard.f8Key.wasPressedThisFrame;
        } catch(Exception ex) {
            keyboardAvailable=false;
            Logger.LogWarning("F8 shortcut disabled; replay continues: "+ex.Message);
            return false;
        }
    }
    void MakeWatch(Transform wrist)
    {
        watch=new GameObject("Monster Event Watch (local)"); watch.transform.SetParent(wrist,false);
        watch.transform.localPosition=new Vector3(0,.055f,-.065f);
        var face=GameObject.CreatePrimitive(PrimitiveType.Cube); face.transform.SetParent(watch.transform,false);
        face.transform.localScale=new Vector3(.11f,.018f,.09f); Destroy(face.GetComponent<Collider>());
        face.GetComponent<Renderer>().material.color=new Color(.08f,.12f,.18f);
        var key=GameObject.CreatePrimitive(PrimitiveType.Cube); button=key.transform; button.SetParent(watch.transform,false);
        button.localPosition=new Vector3(0,.018f,0); button.localScale=new Vector3(.09f,.012f,.07f);
        Destroy(key.GetComponent<Collider>()); key.GetComponent<Renderer>().material.color=new Color(.2f,.65f,.3f);
        var label=new GameObject("Label"); label.transform.SetParent(watch.transform,false);
        label.transform.localPosition=new Vector3(0,.027f,0); label.transform.localRotation=Quaternion.Euler(90,0,0);
        text=label.AddComponent<TextMesh>(); text.anchor=TextAnchor.MiddleCenter; text.alignment=TextAlignment.Center;
        text.characterSize=.006f; text.fontSize=40; text.text="REPLAY v0.3.3\nEVENT";
    }
    void Status(string message) { if(text!=null) text.text=message; if(lastStatus!=message) { Logger.LogInfo(message.Replace('\n',' ')); lastStatus=message; } }
    bool EventReady() => Find<GorillaEventAnimationController>(ExperiencePath)!=null && Resources.FindObjectsOfTypeAll<TimedUnityEventDispatcher>().Count(Target)==2;
    float PlaybackDuration()
    {
        var c=Find<GorillaEventAnimationController>(ExperiencePath); if(c==null) return 900;
        float sum=0; foreach(var clip in c.clips) { if(clip.clip!=null) sum+=clip.clip.length; if((int)clip.onFinalFrame==0) break; }
        return Mathf.Clamp(sum+15,30,3500);
    }
    void Press()
    {
        if(replay) return;
        if(!EventReady()) { Status("ENTER CITY\nEVENT NOT FOUND"); return; }
        if(InRoom) {
            if(!PrivateRoom) { Status("PRIVATE CODE\nREQUIRED"); return; }
            if(PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(SessionKey,out var value) && ReplaySession.TryParse(value as string,out var current) && GroupBusy(current)) { Status("GROUP STILL\nPLAYING"); return; }
            sentRequest=ReplaySession.Request(PhotonNetwork.CurrentRoom.Name,PhotonNetwork.Time);
            PhotonNetwork.LocalPlayer.SetCustomProperties(new Hash {{VersionKey,3},{ReadyKey,true},{RequestKey,sentRequest}});
            Status("REQUESTING\nGROUP REPLAY"); return;
        }
        if(Offline) Prepare(false,Time.realtimeSinceStartupAsDouble+5);
    }
    string Property(Player player,string key) => player.CustomProperties.TryGetValue(key,out var v) ? v as string : null;
    bool Compatible(Player p) => p.CustomProperties.TryGetValue(VersionKey,out var v) && v is int version && version==3;
    bool Ready(Player p) => Compatible(p) && p.CustomProperties.TryGetValue(ReadyKey,out var v) && v is bool ready && ready;
    bool GroupBusy(ReplaySession session) => session.Busy(PhotonNetwork.Time) && (PhotonNetwork.Time<session.Start+3 || PhotonNetwork.PlayerList.Any(p=>Property(p,RunKey)==session.Id));
    void SyncTick()
    {
        if(!PrivateRoom) { lastRoom=null; seenSession=null; sentRequest=null; requests.Clear(); return; }
        string room=PhotonNetwork.CurrentRoom.Name;
        if(lastRoom!=room) { lastRoom=room; seenSession=null; sentRequest=null; requests.Clear(); }
        bool ready=EventReady();
        var local=PhotonNetwork.LocalPlayer;
        if(!Compatible(local) || !local.CustomProperties.TryGetValue(ReadyKey,out var r) || !(r is bool old) || old!=ready)
            local.SetCustomProperties(new Hash {{VersionKey,3},{ReadyKey,ready}});
        var players=PhotonNetwork.PlayerList.Where(Ready).OrderBy(p=>p.ActorNumber).ToArray();
        string wire=PhotonNetwork.CurrentRoom.CustomProperties.TryGetValue(SessionKey,out var value) ? value as string : null;
        ReplaySession.TryParse(wire,out var session);
        double now=PhotonNetwork.Time;
        if(session!=null && session.Id!=seenSession) {
            seenSession=session.Id;
            var host=PhotonNetwork.CurrentRoom.GetPlayer(session.Host);
            if(host!=null && Compatible(host) && !replay && session.CanJoin(local.ActorNumber,now) && ready) {
                Logger.LogInfo("Joining group replay "+session.Id+" with actors "+string.Join(",",session.Actors));
                activeSession=session.Id;
                Prepare(true,session.Start);
            }
        }
        if(players.Length==0 || ReplaySession.Coordinator(players.Select(p=>p.ActorNumber).ToArray())!=local.ActorNumber) return;
        bool requested=false;
        foreach(var player in players) {
            string request=Property(player,RequestKey);
            if(request==null || requests.TryGetValue(player.ActorNumber,out string previous) && previous==request) continue;
            requests[player.ActorNumber]=request;
            // Room-bound expiring requests prevent restarts after a coordinator changes or a player switches codes.
            if(ReplaySession.FreshRequest(request,room,now)) requested=true;
        }
        if(!requested || replay || session!=null && GroupBusy(session)) return;
        var next=new ReplaySession { Id=Guid.NewGuid().ToString("N"), Start=now+7, Duration=PlaybackDuration(), Host=local.ActorNumber, Actors=players.Select(p=>p.ActorNumber).ToArray() };
        PhotonNetwork.CurrentRoom.SetCustomProperties(new Hash {{SessionKey,next.Encode()}});
        Logger.LogInfo("Scheduled group replay for "+players.Length+" mod users in this code");
    }
    void Prepare(bool group,double deadline)
    {
        try {
            controller=Find<GorillaEventAnimationController>(ExperiencePath);
            var targets=Resources.FindObjectsOfTypeAll<TimedUnityEventDispatcher>().Where(Target).ToArray();
            cityMarker=Find<TeleportMarker>(EventPath+"/Cityside/TeleportBackToCity");
            if(controller==null || targets.Length!=2 || controller.controllingAnimation==null || controller.clips.Count==0) throw new InvalidOperationException("City event unavailable");
            shared=group; replayRoom=group?PhotonNetwork.CurrentRoom.Name:null;
            spawners=Resources.FindObjectsOfTypeAll<EntitySpawner>().Where(EventObject).ToArray();
            coreManagers=Resources.FindObjectsOfTypeAll<EntitySpawnManager>().Where(EventObject).ToArray();
            timers.Clear(); foreach(var timer in targets) timers.Add(new TimerSnapshot(timer));
            replay=true; playing=false; coresInitialized=false;
            if(group) PhotonNetwork.LocalPlayer.SetCustomProperties(new Hash {{RunKey,activeSession}});
            foreach(var timer in timers) timer.Pause();
            ClearCores(); ResetAnimation();
            routine=StartCoroutine(Begin(deadline));
        } catch(Exception ex) { Logger.LogError(ex); StopReplay(false,"START FAILED"); }
    }
    void ResetAnimation()
    {
        var stabilizer=controller.GetComponent<CounterRotator>();
        bool stabilize=stabilizer!=null && stabilizer.enabled;
        // CounterRotator accumulates compensation on the event root every LateUpdate.
        // Suspend it while restoring a consistent ship/world starting frame.
        if(stabilizer!=null) stabilizer.enabled=false;
        try {
        Set(controller,"playAnimation",false); Set(controller,"suspended",0f); Set(controller,"animationTime",0f);
        Set(controller,"lateStart",0f); Set(controller,"animationClipIndex",0); Set(controller,"currentClip",null);
        controller.controllingAnimation.Stop();
        var subAnimations=Resources.FindObjectsOfTypeAll<GorillaEventAnimation>().Where(EventObject).ToArray();
        foreach(var sub in subAnimations) {
            if(sub._animation!=null) sub._animation.Stop(); sub._clipIndex=-1;
        }
        // Verified against this Meta City scene: these structural roots are authored
        // at local zero / identity / unit scale. The post-event TestShipMovement clip
        // leaves 1ShipRelativeObjects yawed 90 degrees; clips A-D never key that parent.
        ResetStructuralRoot(controller.transform);
        foreach(string relative in new[]{"Spaceship_Prefab","Spaceship_Prefab/1ShipRelativeObjects","Spaceship_Prefab/2WorldRelativeObjects"}) {
            var root=Find<Transform>(ExperiencePath+"/"+relative);
            if(root==null) throw new InvalidOperationException("Missing event motion root: "+relative);
            ResetStructuralRoot(root);
        }
        // Clip A controls only Ironback. Rewinding only its baked tracks leaves the
        // cockpit, hangar and aliens at poses inherited from later phases or a prior run.
        foreach(var sub in subAnimations) {
            if(sub._animation==null || sub.clips==null || sub.clips.Length==0 || sub.clips[0]==null) continue;
            sub.clips[0].SampleAnimation(sub._animation.gameObject,0f);
        }
        foreach(var seq in Resources.FindObjectsOfTypeAll<SimpleEventSequencer>().Where(EventObject)) Set(seq,"idx",-1);
        var first=controller.clips[0].clip; first.legacy=true;
        controller.controllingAnimation.enabled=true; controller.controllingAnimation.Play(first.name);
        controller.controllingAnimation[first.name].time=0; controller.controllingAnimation.Sample(); controller.controllingAnimation.Stop();
        Set(controller,"currentClip",null);
        for(var t=controller.transform;t!=null && PathOf(t).StartsWith(EventPath,StringComparison.Ordinal);t=t.parent) t.gameObject.SetActive(true);
        controller.enabled=true;
        if(stabilizer!=null) {
            var stabilized=Get(stabilizer,"stabilizedObject") as GameObject;
            if(stabilized==null) throw new InvalidOperationException("Missing stabilized cockpit");
            Set(stabilizer,"startingPosition",stabilized.transform.position);
            Set(stabilizer,"startingRotation",stabilized.transform.rotation);
            Set(stabilizer,"pendingPlayerMovement",Vector3.zero); Set(stabilizer,"appliedCarryVelocity",Vector3.zero);
            Set(stabilizer,"targetGravityTilt",Vector3.zero); Set(stabilizer,"smoothedGravityTilt",Vector3.zero);
            Set(stabilizer,"lastPlayerMovementTime",float.NegativeInfinity);
            stabilizer.enabled=stabilize;
        }
        Logger.LogInfo("Reset authored ship/world roots, all "+subAnimations.Length+" sub-animations and cockpit stabilization anchor");
        } finally { if(stabilizer!=null) stabilizer.enabled=stabilize; }
    }
    static void ResetStructuralRoot(Transform root) { root.localPosition=Vector3.zero; root.localRotation=Quaternion.identity; root.localScale=Vector3.one; }
    IEnumerator Begin(double deadline)
    {
        while(ContextValid && controller!=null) {
            double remaining=deadline-(shared?PhotonNetwork.Time:Time.realtimeSinceStartupAsDouble);
            if(remaining<=0) break;
            Status("START IN\n"+Math.Ceiling(remaining)); yield return null;
        }
        if(!ContextValid || controller==null) { routine=null; StopReplay(false,"CANCELLED"); yield break; }
        try {
            DateTime now=GorillaComputer.instance.GetServerTime();
            if(now.Year<2000) throw new InvalidOperationException("Server clock not ready");
            // Rebase all clients to the same Photon start; keep the timer's 18-second teleport live.
            double late=Math.Max(0,(shared?PhotonNetwork.Time:Time.realtimeSinceStartupAsDouble)-deadline);
            foreach(var timer in timers) timer.Start(now.AddSeconds(-late));
            // Explicitly start a fresh AnimationState as well: a stopped state from a
            // cancelled replay can otherwise retain its clip/weight and never restart.
            var first=controller.clips[0].clip;
            controller.controllingAnimation.Play(first.name);
            controller.controllingAnimation[first.name].time=(float)late;
            controller.StartPlaying((float)late);
            playing=true; beganAt=Time.unscaledTime; watch?.SetActive(false);
            Status("REPLAYING\nWATCH HIDDEN");
        } catch(Exception ex) { Logger.LogError(ex); StopReplay(true,"TIMER FAILED"); }
        routine=null;
    }
    void InitializeCores()
    {
        if(coresInitialized || Time.unscaledTime<nextCoreCheck || GorillaTagger.Instance.offlineVRRig.zoneEntity.currentZone!=GTZone.eventZone) return;
        nextCoreCheck=Time.unscaledTime+1;
        var manager=GameEntityManager.GetManagerForZone(GTZone.eventZone);
        if(manager==null) return;
        if(!shared && !manager.IsZoneActive()) {
            offlineManager=manager;
            object data=Get(manager,"zoneStateData"); offlineZoneState=Get(data,"state");
            var stateType=offlineZoneState.GetType();
            AccessTools.Method(typeof(GameEntityManager),"SetZoneState").Invoke(manager,new[]{Enum.Parse(stateType,"Active")});
        }
        if(!manager.IsZoneActive()) return; // Native networking establishes authority and zone state.
        AccessTools.Method(typeof(GameEntityManager),"RegisterScenePlacedEntities").Invoke(manager,null);
        // A previous offline run can leave registered scene entities disabled. Re-adopt only
        // event entities that the game originally assigned to this zone, without changing authority.
        foreach(var entity in Resources.FindObjectsOfTypeAll<GameEntity>().Where(EventObject))
            if(entity.IsScenePlaced && entity.manager==null && (GTZone)Get(entity,"scenePlacedZone")==GTZone.eventZone)
                AccessTools.Method(typeof(GameEntityManager),"RegisterSingleScenePlacedEntity").Invoke(manager,new object[]{entity});
        bool ready=true;
        foreach(var core in coreManagers) {
            if(core==null) continue;
            var entity=core.GetComponent<GameEntity>();
            if(entity==null || entity.manager==null) { ready=false; continue; }
            core.gameObject.SetActive(true);
            AccessTools.Method(typeof(EntitySpawnManager),"SetIsAuthority").Invoke(core,new object[]{entity.manager.IsAuthority()});
            AccessTools.Method(typeof(EntitySpawnManager),"SetZoneActive").Invoke(core,new object[]{entity.manager.IsZoneActive()});
            Logger.LogInfo("Core manager ready: native authority="+entity.manager.IsAuthority()+", zone="+entity.manager.zone);
        }
        foreach(var spawner in spawners) if(spawner!=null && spawner.isActiveAndEnabled && !spawner.IsSpawning) spawner.StartSpawning();
        coresInitialized=ready;
    }
    void ClearCores()
    {
        foreach(var spawner in spawners) {
            if(spawner==null) continue;
            spawner.StopSpawning();
            var list=(List<SpawnableEntity>)Get(spawner,"_spawns");
            foreach(var spawn in list.ToArray()) if(spawn!=null && spawn.entity!=null && spawn.entity.manager!=null && spawn.entity.manager.IsAuthority()) spawn.entity.manager.RequestDestroyItem(spawn.entity.id);
            list.RemoveAll(x=>x==null);
            Set(spawner,"_spawnAccumulator",0f);
        }
    }
    void StopReplay(bool returnToCity,string reason)
    {
        if(stopping) return; stopping=true;
        try {
            if(routine!=null) { StopCoroutine(routine); routine=null; }
            bool wasReplay=replay; replay=false; playing=false;
            if(shared && PrivateRoom && PhotonNetwork.CurrentRoom.Name==replayRoom) PhotonNetwork.LocalPlayer.SetCustomProperties(new Hash {{RunKey,null}});
            if(wasReplay && controller!=null) Cleanup(()=>{
                Set(controller,"playAnimation",false); Set(controller,"suspended",0f); controller.controllingAnimation.Stop();
                foreach(var sub in Resources.FindObjectsOfTypeAll<GorillaEventAnimation>().Where(EventObject)) if(sub._animation!=null) sub._animation.Stop();
            },"stop animations");
            if(wasReplay) Cleanup(ClearCores,"clear event cores");
            foreach(var timer in timers) Cleanup(timer.Restore,"restore timer"); timers.Clear();
            if(wasReplay && returnToCity && (Offline || PrivateRoom) && cityMarker!=null) Cleanup(cityMarker.TeleportLocalPlayerToMark,"return to City");
            if(offlineManager!=null && Offline && offlineZoneState!=null) Cleanup(()=>AccessTools.Method(typeof(GameEntityManager),"SetZoneState").Invoke(offlineManager,new[]{offlineZoneState}),"restore offline zone");
            offlineManager=null; offlineZoneState=null; coresInitialized=false;
            shared=false; activeSession=null;
            if(watch!=null) watch.SetActive(true);
            nextPress=Time.unscaledTime+2; touching=true;
            Logger.LogInfo("Replay ended: "+reason);
            Status("REPLAY v0.3.3\nEVENT");
        } finally { stopping=false; }
    }
    void Cleanup(Action action,string label) { try { action(); } catch(Exception ex) { Logger.LogWarning("Cleanup could not "+label+": "+ex); } }
    void OnDestroy()
    {
        StopReplay(false,"PLUGIN UNLOADED");
        if(PrivateRoom) PhotonNetwork.LocalPlayer.SetCustomProperties(new Hash {{VersionKey,null},{ReadyKey,null},{RequestKey,null},{RunKey,null}});
        patches?.UnpatchSelf(); if(watch!=null) Destroy(watch); self=null;
    }
    sealed class TimerSnapshot
    {
        public readonly TimedUnityEventDispatcher Timer;
        readonly IList list;
        readonly object[] contents;
        readonly object ready;
        readonly int index;
        readonly bool enabled;
        readonly List<Tuple<object,object>> dates=new List<Tuple<object,object>>();
        public TimerSnapshot(TimedUnityEventDispatcher timer) {
            Timer=timer; list=(IList)Get(timer,"nodeList"); contents=new object[list.Count]; list.CopyTo(contents,0);
            ready=Get(timer,"readyState"); index=(int)Get(timer,"activeNodeIndex"); enabled=timer.enabled;
            foreach(object node in (Array)Get(timer,"nodes")) dates.Add(Tuple.Create(node,Get(node,"<ActivationTime>k__BackingField")));
        }
        public void Pause() { Set(Timer,"readyState",Enum.ToObject(ready.GetType(),0)); }
        public void Start(DateTime now) {
            for(var t=Timer.transform;t!=null && t.name!="City_Pretty";t=t.parent) t.gameObject.SetActive(true);
            Timer.enabled=true;
            AccessTools.Method(typeof(TimedUnityEventDispatcher),"setStartDate").Invoke(Timer,new object[]{now});
            Set(Timer,"readyState",Enum.ToObject(ready.GetType(),2));
            self.Logger.LogInfo("Reset City timer: "+PathOf(Timer.transform));
        }
        public void Restore() {
            if(Timer==null) return;
            foreach(var date in dates) Set(date.Item1,"<ActivationTime>k__BackingField",date.Item2);
            list.Clear(); foreach(var item in contents) list.Add(item);
            Set(Timer,"nodeList",list); Set(Timer,"activeNodeIndex",index); Set(Timer,"readyState",ready); Timer.enabled=enabled;
        }
    }
    [HarmonyPatch(typeof(TimedUnityEventDispatcher),"onDateRetrieved")]
    class DateCallbackPatch { static bool Prefix(TimedUnityEventDispatcher __instance) => self==null || !self.replay || !Target(__instance); }
    [HarmonyPatch(typeof(TimedUnityEventDispatcher),"IGorillaSliceableSimple.SliceUpdate")]
    class ContextGuardPatch { static bool Prefix(TimedUnityEventDispatcher __instance) => self==null || !self.replay || !Target(__instance) || self.ContextValid; }
    [HarmonyPatch(typeof(EntitySpawnManager),"get_CanSpawn")]
    class CoreAuthorityPatch { static bool Prefix(EntitySpawnManager __instance,ref bool __result) {
        if(self==null || !self.replay || !self.ContextValid || !EventObject(__instance)) return true;
        var entity=__instance.GetComponent<GameEntity>(); var manager=entity!=null?entity.manager:null;
        __result=manager!=null && manager.IsAuthority() && manager.IsZoneActive(); return false;
    } }
}

