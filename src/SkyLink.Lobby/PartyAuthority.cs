using System.Security.Cryptography;

namespace SkyLink.Lobby;

public enum JoinOutcome { Accepted, Rejoined, Full, InFlightLocked }

/// <summary>
/// The host's authoritative copy of the party. Every change, whoever asks for it, goes through
/// <see cref="Apply"/>, which validates it and bumps <see cref="PartyState.Version"/>; members
/// only ever display the latest version they received. Pure (no I/O), so it is tested directly.
/// </summary>
public sealed class PartyAuthority
{
    public const int MaxMembers = 2;

    private readonly Dictionary<string, List<CatalogAircraft>> _catalogs = [];
    private PartyState _state;

    public PartyState State => _state;
    public IReadOnlyList<CatalogAircraft> SharedCatalog { get; private set; } = [];

    public PartyAuthority(string hostId, string hostName, string partyName, string? partyId = null)
    {
        partyId ??= Convert.ToHexString(RandomNumberGenerator.GetBytes(6));
        var host = new MemberInfo(hostId, hostName, "", true, false, null, 0, null, false);
        _state = new PartyState(partyId, partyName, 1, hostId, [host], hostId, null, FlightSetup.Empty, PartyPhase.Lobby, null, 0);
    }

    // ------------------------------------------------------------------ membership

    public JoinOutcome Join(string id, string name, string address)
    {
        var existing = _state.Member(id);
        if (existing is not null)
        {
            Commit(_state with { Members = Replace(existing with { Name = name, Address = address, Connected = true }) });
            return JoinOutcome.Rejoined; // same seat as before (e.g. after a network blip)
        }
        if (_state.Members.Count >= MaxMembers) return JoinOutcome.Full;
        if (_state.Phase != PartyPhase.Lobby) return JoinOutcome.InFlightLocked;

        var member = new MemberInfo(id, name, address, true, false, null, 0, null, false);
        var s = _state with { Members = [.. _state.Members, member] };
        if (s.CaptainId is null) s = s with { CaptainId = id };
        else if (s.FirstOfficerId is null) s = s with { FirstOfficerId = id };
        Commit(s);
        return JoinOutcome.Accepted;
    }

    /// <summary>The member's connection dropped: keep the seat (they may come back), mark offline.</summary>
    public void Disconnected(string id)
    {
        if (_state.Member(id) is { Connected: true } m) Commit(_state with { Members = Replace(m with { Connected = false }) });
    }

    /// <summary>The member left (or was kicked): free the seat.</summary>
    public void Remove(string id)
    {
        if (id == _state.HostId || _state.Member(id) is null) return;
        _catalogs.Remove(id);
        var s = _state with
        {
            Members = _state.Members.Where(m => m.Id != id).ToList(),
            CaptainId = _state.CaptainId == id ? null : _state.CaptainId,
            FirstOfficerId = _state.FirstOfficerId == id ? null : _state.FirstOfficerId,
        };
        if (s.Phase != PartyPhase.Lobby) s = BackToLobby(s);
        // The remaining pilot keeps their seat; an empty Captain seat is refilled by them.
        if (s.CaptainId is null && s.FirstOfficerId is { } fo) s = s with { CaptainId = fo, FirstOfficerId = null };
        Commit(s);
        RecomputeCatalog();
    }

    // ------------------------------------------------------------------ requests

    /// <summary>Apply a request from <paramref name="from"/>. Returns null on success, else why not.</summary>
    public string? Apply(string from, PartyIntent intent)
    {
        var me = _state.Member(from);
        if (me is null) return "not a member of this party";
        var seat = _state.SeatOf(from);
        bool seated = seat != Seat.None;
        bool lobby = _state.Phase == PartyPhase.Lobby;
        var setup = _state.Setup;

        switch (intent)
        {
            case TakeSeat t:
            {
                if (t.Seat == Seat.None || t.Seat == seat) return null;
                string? other = t.Seat == Seat.Captain ? _state.CaptainId : _state.FirstOfficerId;
                var s = t.Seat == Seat.Captain
                    ? _state with { CaptainId = from, FirstOfficerId = other ?? (_state.FirstOfficerId == from ? null : _state.FirstOfficerId) }
                    : _state with { FirstOfficerId = from, CaptainId = other ?? (_state.CaptainId == from ? null : _state.CaptainId) };
                Commit(s);
                return null;
            }
            case SwapSeats:
                if (!seated) return "only the pilots can swap seats";
                Commit(_state with { CaptainId = _state.FirstOfficerId, FirstOfficerId = _state.CaptainId });
                return null;

            case SetAircraft a:
            {
                if (Locked(seated, lobby) is { } why) return why;
                var entry = SharedCatalog.FirstOrDefault(c => c.Title == a.Title);
                if (entry is null) return $"'{a.Title}' is not installed on both PCs";
                if (!entry.Liveries.Contains(a.Livery)) return $"livery '{a.Livery}' is not installed on both PCs";
                Commit(_state with { Setup = setup with { AircraftTitle = a.Title, Livery = a.Livery, EditedBy = from } });
                return null;
            }
            case SetCallsign c:
                if (Locked(seated, lobby) is { } why2) return why2;
                Commit(_state with
                {
                    Setup = setup with
                    {
                        Callsign = Clean(c.Callsign, 12),
                        AirlineCallsign = Clean(c.AirlineCallsign, 32),
                        FlightNumber = Clean(c.FlightNumber, 8),
                        EditedBy = from,
                    },
                });
                return null;
            case SetPlan p:
            {
                if (Locked(seated, lobby) is { } why3) return why3;
                var next = setup with { Plan = p.Plan, EditedBy = from };
                // A fresh plan fills in identity fields the pilots haven't set themselves.
                if (p.Plan is { } plan)
                    next = next with
                    {
                        Callsign = next.Callsign ?? Clean(plan.Callsign, 12),
                        FlightNumber = next.FlightNumber ?? Clean(plan.FlightNumber, 8),
                    };
                Commit(_state with { Setup = next });
                return null;
            }
            case SetDeparture d:
                if (Locked(seated, lobby) is { } why4) return why4;
                Commit(_state with { Setup = setup with { DepartureIcao = Clean(d.Icao, 4)?.ToUpperInvariant(), EditedBy = from } });
                return null;

            case UpdateStatus u:
            {
                var s = _state with
                {
                    Members = Replace(me with { SimRunning = u.SimRunning, SimAircraft = u.SimAircraft, InCockpit = u.InCockpit, LaunchStage = u.LaunchStage }),
                };
                // Both pilots in their cockpits: the flight is on.
                if (s.Phase == PartyPhase.Launching && s.Captain is { InCockpit: true } && s.FirstOfficer is { InCockpit: true })
                    s = s with { Phase = PartyPhase.InFlight };
                Commit(s);
                return null;
            }
            case PublishCatalog pc:
                _catalogs[from] = pc.Aircraft.Select(a => a with
                {
                    DisplayName = Clean(a.DisplayName, 128), Developer = Clean(a.Developer, 64), Manufacturer = Clean(a.Manufacturer, 64),
                }).ToList();
                Commit(_state with { Members = Replace(me with { CatalogCount = pc.Aircraft.Count }) });
                RecomputeCatalog();
                return null;

            case StartFlight sf:
            {
                if (seat != Seat.Captain) return "only the Captain can start the flight";
                if (CanStart() is { } why5) return why5;
                Commit(_state with
                {
                    Phase = PartyPhase.Launching,
                    SessionCode = SkyLink.Net.Link.NewSessionCode(),
                    LaunchStart = sf.Start,
                    Members = _state.Members.Select(m => m with { InCockpit = false, LaunchStage = "starting" }).ToList(),
                });
                return null;
            }
            case EndFlight:
                if (!seated) return "only the pilots can end the flight";
                if (_state.Phase == PartyPhase.Lobby) return null;
                Commit(BackToLobby(_state));
                return null;

            case Kick k:
                if (from != _state.HostId) return "only the party host can remove someone";
                if (k.MemberId == _state.HostId) return "the host cannot remove themselves";
                Remove(k.MemberId);
                return null;
            case RenameParty r:
                if (from != _state.HostId) return "only the party host can rename the party";
                Commit(_state with { Name = Clean(r.Name, 40) ?? _state.Name });
                return null;
        }
        return "unknown request";
    }

    /// <summary>Null when the Captain may press Start, else what is missing (shown in the UI).</summary>
    public string? CanStart()
    {
        var s = _state;
        if (s.Members.Count < MaxMembers || s.Captain is null || s.FirstOfficer is null) return "waiting for a First Officer";
        if (s.Members.FirstOrDefault(m => !m.Connected) is { } off) return $"{off.Name} is offline";
        if (s.Setup.AircraftTitle is null || s.Setup.Livery is null) return "choose an aircraft and livery";
        var entry = SharedCatalog.FirstOrDefault(c => c.Title == s.Setup.AircraftTitle);
        if (entry is null || !entry.Liveries.Contains(s.Setup.Livery)) return "the chosen aircraft/livery is no longer installed on both PCs";
        if (s.Phase != PartyPhase.Lobby) return "a flight is already running";
        return null;
    }

    private static string? Locked(bool seated, bool lobby) =>
        !seated ? "only the Captain and First Officer can change the flight"
        : !lobby ? "the flight setup is locked while flying; end the flight to change it"
        : null;

    private static PartyState BackToLobby(PartyState s) => s with
    {
        Phase = PartyPhase.Lobby,
        SessionCode = null,
        LaunchStart = null,
        Members = s.Members.Select(m => m with { InCockpit = false, LaunchStage = null }).ToList(),
    };

    private void RecomputeCatalog()
    {
        var lists = _state.Members.Select(m => _catalogs.GetValueOrDefault(m.Id)).ToList();
        List<CatalogAircraft> shared;
        if (lists.Any(l => l is null))
        {
            // Someone hasn't reported yet: show what is known, but nothing can be both-installed.
            shared = lists.Count(l => l is not null) == 1 && _state.Members.Count == 1 ? lists[0]! : [];
        }
        else
        {
            // Matched by title and livery name only; the friendly name/developer comes from whichever
            // PC could resolve it (both PCs then show the same).
            shared = lists[0]!
                .Where(a => lists.Skip(1).All(l => l!.Any(b => b.Title == a.Title)))
                .Select(a =>
                {
                    var same = lists.Select(l => l!.First(b => b.Title == a.Title)).ToList();
                    return new CatalogAircraft(a.Title,
                        a.Liveries.Where(lv => same.Skip(1).All(b => b.Liveries.Contains(lv))).Order(StringComparer.OrdinalIgnoreCase).ToList(),
                        same.Select(b => b.DisplayName).FirstOrDefault(n => n is not null),
                        same.Select(b => b.Developer).FirstOrDefault(n => n is not null),
                        same.Select(b => b.Manufacturer).FirstOrDefault(n => n is not null));
                })
                .Where(a => a.Liveries.Count > 0)
                .OrderBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        bool changed = shared.Count != SharedCatalog.Count
                       || shared.Zip(SharedCatalog).Any(p => p.First.Title != p.Second.Title || !p.First.Liveries.SequenceEqual(p.Second.Liveries)
                                                            || p.First.DisplayName != p.Second.DisplayName || p.First.Developer != p.Second.Developer
                                                            || p.First.Manufacturer != p.Second.Manufacturer);
        if (!changed) return;
        SharedCatalog = shared;
        Commit(_state with { CatalogVersion = _state.CatalogVersion + 1 });
    }

    private List<MemberInfo> Replace(MemberInfo m) => _state.Members.Select(x => x.Id == m.Id ? m : x).ToList();

    private static string? Clean(string? s, int max)
    {
        s = s?.Trim();
        return string.IsNullOrEmpty(s) ? null : s.Length > max ? s[..max] : s;
    }

    private void Commit(PartyState next) => _state = next with { Version = _state.Version + 1 };
}
