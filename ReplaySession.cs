using System;
using System.Globalization;
using System.Linq;

// Independent of Unity/Photon so the wire format and join rules can be tested locally.
internal sealed class ReplaySession
{
    internal string Id;
    internal double Start, Duration;
    internal int Host;
    internal int[] Actors;
    internal string Encode() => string.Join("|", "3", Id, Start.ToString("R",CultureInfo.InvariantCulture), Duration.ToString("R",CultureInfo.InvariantCulture), Host.ToString(CultureInfo.InvariantCulture), string.Join(",",Actors));
    internal bool CanJoin(int actor,double now) => Actors.Contains(actor) && now <= Start && Start-now <= 15;
    internal bool Busy(double now) => now < Start+Duration+5;
    internal static int Coordinator(int[] actors) => actors.Length == 0 ? -1 : actors.Min();
    internal static string Request(string room,double now) => Guid.NewGuid().ToString("N")+"|"+now.ToString("R",CultureInfo.InvariantCulture)+"|"+Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(room));
    internal static bool FreshRequest(string request,string room,double now)
    {
        if(string.IsNullOrEmpty(request) || request.Length>1024) return false;
        var parts=request.Split('|');
        if(parts.Length!=3 || !Guid.TryParseExact(parts[0],"N",out _) || !double.TryParse(parts[1],NumberStyles.Float,CultureInfo.InvariantCulture,out double time) || double.IsNaN(time) || double.IsInfinity(time) || now-time< -2 || now-time>10) return false;
        try { return System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[2]))==room; } catch(FormatException) { return false; }
    }
    internal static bool TryParse(string wire,out ReplaySession session)
    {
        session=null;
        if(string.IsNullOrEmpty(wire) || wire.Length>2048) return false;
        var p=wire.Split('|');
        if(p.Length!=6 || p[0]!="3" || !Guid.TryParseExact(p[1],"N",out _)) return false;
        if(!double.TryParse(p[2],NumberStyles.Float,CultureInfo.InvariantCulture,out double start) || double.IsNaN(start) || double.IsInfinity(start) || start<0) return false;
        if(!double.TryParse(p[3],NumberStyles.Float,CultureInfo.InvariantCulture,out double duration) || double.IsNaN(duration) || double.IsInfinity(duration) || duration<1 || duration>3600) return false;
        if(!int.TryParse(p[4],out int host) || host<1) return false;
        var a=p[5].Split(','); if(a.Length<1 || a.Length>100) return false;
        var actors=new int[a.Length];
        for(int i=0;i<a.Length;i++) if(!int.TryParse(a[i],out actors[i]) || actors[i]<1) return false;
        if(actors.Distinct().Count()!=actors.Length || !actors.Contains(host)) return false;
        session=new ReplaySession { Id=p[1], Start=start, Duration=duration, Host=host, Actors=actors }; return true;
    }
}
