using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Serialization;
using NLog;
using ProtoBuf;
using Sandbox.Game;
using Sandbox.Game.Entities;
using Sandbox.Game.Entities.Blocks;
using Sandbox.Game.GUI;
using Sandbox.Game.GameSystems.BankingAndCurrency;
using Sandbox.Game.World;
using Sandbox.ModAPI;
using VRage.Game;
using VRage.Game.Entity;
using VRageMath;

namespace VirtualGarage.Market
{
    /// <summary>
    /// A message of the trade console (the ScriptPack's "Торговая консоль", Shipyard_block). The same contract
    /// is in the mod (PublicShipyard.MarketMessage): the members' numbers are the wire.
    /// </summary>
    [ProtoContract]
    public class MarketMessage
    {
        [ProtoMember(1)] public byte Type;
        [ProtoMember(2)] public long ShipyardId;
        [ProtoMember(3)] public long Price;
        [ProtoMember(4)] public string Name;
        [ProtoMember(5)] public List<string> Names;
        [ProtoMember(6)] public bool Active;
        [ProtoMember(7)] public string Seller;
        [ProtoMember(8)] public string Text;
        [ProtoMember(9)] public bool Pending;
    }

    public static class MarketMessageType
    {
        // client to server
        public const byte ListReq = 1;
        public const byte SellReq = 2;
        public const byte BuyReq = 3;
        public const byte CancelReq = 4;
        public const byte StateReq = 5;

        // server to client
        public const byte GarageList = 10;
        public const byte State = 11;
        public const byte Text = 12;
    }

    /// <summary>
    /// A ship on sale: its garage file waits at the seller's, hidden from !g list, until it is bought or taken back.
    /// Bought, it is a purchase the world has not saved yet: the file is in the buyer's garage, the console free.
    /// </summary>
    public class Listing
    {
        public long ShipyardId;
        public ulong SellerSteamId;
        public long SellerIdentity;
        public string SellerName;
        public string File;
        public string ShipName;
        public long Price;

        /// <summary>Bought, the world not saved since: the file is in the buyer's garage (<see cref="BuyerFile"/>, or taken out of it).</summary>
        public bool Pending;
        public ulong BuyerSteamId;
        public string BuyerFile;
        public long Operation;

        /// <summary>Where the console stood when the ship was put up: a console moved (or made a ship) takes its ship off.</summary>
        public double ConsoleX, ConsoleY, ConsoleZ;

        /// <summary>Since when the console is not found (FixShip makes it again under another id: it is looked for at its place).</summary>
        [XmlIgnore] public DateTime? MissingSince;

        [XmlIgnore] public Vector3D ConsoleAt
        {
            get => new Vector3D(ConsoleX, ConsoleY, ConsoleZ);
            set { ConsoleX = value.X; ConsoleY = value.Y; ConsoleZ = value.Z; }
        }
    }

    /// <summary>
    /// The trade console, on the server. A player puts a ship from his garage up for sale on his console at his
    /// price - a ship put into the garage near the console (<see cref="NearConsole"/>), on a console of a station;
    /// another player, not of the seller's faction, buys it with credits: the seller is paid and the ship is in the
    /// buyer's garage at once, as a
    /// bought file (<see cref="GarageFiles.IsBought"/>: taken out free, next to the player wherever he is, whatever
    /// the garage's settings). All of it is decided here, from the client's real sender (the console's old server
    /// part, in SentisOptimisations, took the buyer and the price from the client's message). So a ship goes no
    /// further than to the console by a sale: a faction does not move its ships across the map by selling them to
    /// itself, and the console sells only where it stands - a console made a ship, or moved, takes its ship off.
    ///
    /// A crash neither loses nor copies a ship (<see cref="GarageFiles"/>): the money moves in the world, the file
    /// on disk. Until a world save that saw the purchase is written the purchase is remembered; a crash before that
    /// undoes it as the world gives the buyer back his money and takes the seller's: the file - in the buyer's
    /// garage, or taken out of it (the ship is not in the loaded world) - goes back into the seller's garage.
    /// </summary>
    public static class ShipMarket
    {
        public static readonly Logger Log = LogManager.GetCurrentClassLogger();

        public const ushort Channel = 10777;
        public const string ConsoleSubtype = "Shipyard_block";
        private const string ListedSuffix = "_market";
        private const string Extension = ".sbc";
        private const string Unsaved = "_unsaved.sbc";
        private const long MaxPrice = 1_000_000_000_000L;

        /// <summary>How near the console a ship on sale was put into the garage, m.</summary>
        public const double NearConsole = 1000;

        /// <summary>A console further than this from where it stood when its ship was put up has moved, m.</summary>
        private const double MovedBy = 1;

        /// <summary>A console not found that long (gone, not made again in its place) gives its ship back.</summary>
        private static readonly TimeSpan GoneAfter = TimeSpan.FromMinutes(2);

        private const int WatchMs = 10000;

        private static readonly object Lock = new object();
        private static List<Listing> _listings = new List<Listing>();
        private static Action<ushort, byte[], ulong, bool> _handler;
        private static Timer _watch;

        private static string IndexPath => Path.Combine(GarageFiles.Root, "market.xml");

        // ------------------------------------------------------------------ the session

        /// <summary>The world is loaded (game thread).</summary>
        public static void OnLoaded()
        {
            if (_handler == null)
            {
                _handler = OnMessage;
                MyAPIGateway.Multiplayer.RegisterSecureMessageHandler(Channel, _handler);
                Log.Info("Trade console listening");
            }

            _watch?.Dispose();
            _watch = new Timer(_ => MyAPIGateway.Utilities?.InvokeOnGameThread(Watch), null, WatchMs, WatchMs);
        }

        public static void OnUnloading()
        {
            _watch?.Dispose();
            _watch = null;
            if (_handler == null) return;
            MyAPIGateway.Multiplayer?.UnregisterSecureMessageHandler(Channel, _handler);
            _handler = null;
        }

        /// <summary>
        /// The world was loaded: a purchase after its last save did not happen (the world gave the money back), so
        /// its ship goes back into the seller's garage - from the buyer's garage, or taken out of it (the ship is not
        /// in this world). Not on sale again: the console may sell another one by now. Called before anything else
        /// touches the garage.
        /// </summary>
        public static void OnWorldLoaded()
        {
            lock (Lock)
            {
                _listings = Load();
                foreach (var listing in _listings.Where(l => l.Pending).ToList())
                {
                    _listings.Remove(listing);
                    var back = Unlisted(listing.File);
                    var at = new[] { listing.BuyerFile, listing.BuyerFile + GarageFiles.TakenOutUnsaved }.FirstOrDefault(File.Exists);
                    if (at == null)
                    {
                        Log.Error("Bought after the last world save, the purchase did not happen, but its file is gone: " + listing.BuyerFile);
                        continue;
                    }
                    if (GarageFiles.Rename(at, back))
                        Log.Warn("Bought after the last world save, the purchase did not happen: " + at + " back in the seller's garage as " + back);
                }

                _listings.RemoveAll(l => !File.Exists(l.File));
                Save();
            }
        }

        /// <summary>A world save that saw every garage operation up to <paramref name="last"/> is on disk: those purchases are done.</summary>
        public static void OnWorldSaved(long last)
        {
            lock (Lock)
            {
                var done = _listings.Where(l => l.Pending && l.Operation <= last).ToList();
                if (done.Count == 0) return;
                foreach (var listing in done)
                {
                    _listings.Remove(listing);
                    Log.Info("Purchase saved: " + listing.BuyerFile);
                }

                Save();
            }
        }

        // ------------------------------------------------------------------ the consoles' place (game thread)

        /// <summary>
        /// Every ship on sale, its console looked at: found (under its id, or made again in its place by FixShip), a
        /// station, where it stood. A console made a ship or moved gives the ship back at once, one gone for
        /// <see cref="GoneAfter"/> too.
        /// </summary>
        public static void Watch()
        {
            try
            {
                var now = DateTime.Now;
                lock (Lock)
                {
                    foreach (var listing in _listings.Where(l => !l.Pending).ToList())
                    {
                        var console = Console(listing.ShipyardId) ?? FindAgain(listing);
                        if (console == null)
                        {
                            listing.MissingSince = listing.MissingSince ?? now;
                            if (now - listing.MissingSince.Value > GoneAfter) GiveBack(listing, null, "консоли больше нет");
                            continue;
                        }
                        listing.MissingSince = null;
                        var why = NotInPlace(console, listing);
                        if (why != null) GiveBack(listing, console, why);
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Watching the trade consoles failed");
            }
        }

        /// <summary>Why the console does not sell its ship any more, or null.</summary>
        private static string NotInPlace(MyProjectorBase console, Listing listing)
        {
            if (!console.CubeGrid.IsStatic) return "консоль стала частью корабля";
            if (Vector3D.Distance(console.PositionComp.GetPosition(), listing.ConsoleAt) > MovedBy) return "консоль сдвинута с места";
            return null;
        }

        /// <summary>
        /// The console of a listing made again in its place (FixShip remaps the ids of a grid it makes anew): the same
        /// block of the same owner where the console stood. Taken for the listing, the ship projected on it again.
        /// </summary>
        private static MyProjectorBase FindAgain(Listing listing)
        {
            var sphere = new BoundingSphereD(listing.ConsoleAt, MovedBy);
            var found = new List<MyEntity>();
            MyGamePruningStructure.GetAllTopMostEntitiesInSphere(ref sphere, found);
            foreach (var grid in found.OfType<MyCubeGrid>())
            {
                if (grid.MarkedForClose || grid.IsPreview) continue;
                foreach (var block in grid.GetFatBlocks())
                {
                    if (!(block is MyProjectorBase console) || console.BlockDefinition.Id.SubtypeName != ConsoleSubtype) continue;
                    if (console.OwnerId != listing.SellerIdentity || Vector3D.Distance(console.PositionComp.GetPosition(), listing.ConsoleAt) > MovedBy) continue;
                    if (_listings.Any(l => l != listing && !l.Pending && l.ShipyardId == console.EntityId)) continue;
                    Log.Info($"Trade console {listing.ShipyardId} made again as {console.EntityId}: {listing.ShipName} stays on sale");
                    listing.ShipyardId = console.EntityId;
                    Save();
                    Project(console, listing.File);
                    return console;
                }
            }
            return null;
        }

        /// <summary>The console's own listing, once its place is looked at: one not in its place is given back. Game thread, under the lock.</summary>
        private static Listing ValidListingOf(MyProjectorBase console)
        {
            var listing = ListingOf(console.EntityId);
            if (listing == null)
            {
                // a console made again (FixShip): its listing waits for it at its place
                foreach (var waiting in _listings.Where(l => !l.Pending && Console(l.ShipyardId) == null).ToList())
                    if (FindAgain(waiting) == console) listing = waiting;
                if (listing == null) return null;
            }
            var why = NotInPlace(console, listing);
            if (why == null) return listing;
            GiveBack(listing, console, why);
            return null;
        }

        /// <summary>A ship off sale, back into its seller's garage, and he is told why. Under the lock.</summary>
        private static void GiveBack(Listing listing, MyProjectorBase console, string why)
        {
            var back = Unlisted(listing.File);
            if (!GarageFiles.Rename(listing.File, back)) return;
            _listings.Remove(listing);
            Save();
            Log.Warn($"Trade console {listing.ShipyardId}: {listing.ShipName} off sale ({why}), {back} back in the garage");
            Say(listing.SellerSteamId, "«" + listing.ShipName + "» снят с продажи: " + why + ". Корабль вернулся в ваш гараж");
            if (console == null) return;
            ((IMyProjector)console).SetProjectedGrid(null);
            BroadcastState(console);
        }

        // ------------------------------------------------------------------ the messages (game thread)

        private static void OnMessage(ushort id, byte[] data, ulong sender, bool fromServer)
        {
            try
            {
                if (sender == 0 || data == null || data.Length > 4096) return;
                var message = MyAPIGateway.Utilities.SerializeFromBinary<MarketMessage>(data);
                if (message == null) return;
                var console = Console(message.ShipyardId);
                if (console == null) return;
                var identity = MySession.Static.Players.TryGetIdentityId(sender);
                if (identity == 0) return;

                switch (message.Type)
                {
                    case MarketMessageType.ListReq: SendGarage(console, sender, identity); break;
                    case MarketMessageType.StateReq: SendState(console, sender); break;
                    case MarketMessageType.SellReq: Sell(console, sender, identity, message.Name, message.Price); break;
                    case MarketMessageType.CancelReq: Cancel(console, sender, identity); break;
                    case MarketMessageType.BuyReq: Buy(console, sender, identity); break;
                }
            }
            catch (Exception e)
            {
                Log.Error(e, "Trade console message from " + sender + " failed");
            }
        }

        private static MyProjectorBase Console(long id)
        {
            var projector = Sandbox.Game.Entities.MyEntities.GetEntityById(id) as MyProjectorBase;
            if (projector == null || projector.Closed || projector.MarkedForClose) return null;
            return projector.BlockDefinition.Id.SubtypeName == ConsoleSubtype ? projector : null;
        }

        private static Listing ListingOf(long shipyardId) => _listings.FirstOrDefault(l => !l.Pending && l.ShipyardId == shipyardId);

        /// <summary>
        /// The ships of the seller's garage that can be sold: saved by the world (an unsaved one may still be in it),
        /// and not bought since the last save (a crash gives that one back to its seller).
        /// </summary>
        private static List<string> Sellable(ulong steamId) =>
            GarageFiles.List(steamId).Where(f => !f.EndsWith(Unsaved, StringComparison.OrdinalIgnoreCase) &&
                                                 !_listings.Any(l => l.Pending && string.Equals(l.BuyerFile, f, StringComparison.OrdinalIgnoreCase))).ToList();

        private static void SendGarage(MyProjectorBase console, ulong sender, long identity)
        {
            if (console.OwnerId != identity)
            {
                Say(sender, "Продавать на этой консоли может только её владелец");
                return;
            }

            List<string> names;
            lock (Lock) names = Sellable(sender).Select(GarageFiles.Title).ToList();
            Send(sender, new MarketMessage { Type = MarketMessageType.GarageList, ShipyardId = console.EntityId, Names = names });
            SendState(console, sender);
        }

        private static MarketMessage State(MyProjectorBase console)
        {
            lock (Lock)
            {
                var listing = ValidListingOf(console);
                return listing == null
                    ? new MarketMessage { Type = MarketMessageType.State, ShipyardId = console.EntityId }
                    : new MarketMessage
                    {
                        Type = MarketMessageType.State, ShipyardId = console.EntityId, Active = true, Pending = listing.Pending,
                        Name = listing.ShipName, Price = listing.Price, Seller = listing.SellerName
                    };
            }
        }

        private static void SendState(MyProjectorBase console, ulong to) => Send(to, State(console));

        private static void BroadcastState(MyProjectorBase console)
        {
            var bytes = MyAPIGateway.Utilities.SerializeToBinary(State(console));
            MyAPIGateway.Multiplayer.SendMessageToOthers(Channel, bytes);
        }

        private static void Sell(MyProjectorBase console, ulong sender, long identity, string name, long price)
        {
            if (console.OwnerId != identity) { Say(sender, "Продавать на этой консоли может только её владелец"); return; }
            if (price < 0 || price > MaxPrice) { Say(sender, "Неверная цена"); return; }
            if (string.IsNullOrEmpty(name)) return;
            if (!console.CubeGrid.IsStatic) { Say(sender, "Торговая консоль работает только на станции"); return; }

            string file;
            lock (Lock)
            {
                if (ValidListingOf(console) != null) { Say(sender, "На консоли уже продаётся корабль"); return; }
                file = Sellable(sender).FirstOrDefault(f => GarageFiles.Title(f) == name);
                if (file == null) { Say(sender, "Корабля «" + name + "» нет в гараже (или мир ещё не сохранён после уборки)"); return; }
            }

            // where the ship was put into the garage: in its file (read aside, a big ship is a big file)
            var consoleAt = console.PositionComp.GetPosition();
            Task.Run(() =>
            {
                var grid = ReadMainGrid(file);
                MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                {
                    try
                    {
                        if (grid?.PositionAndOrientation == null) { Say(sender, "Не удалось прочитать корабль из гаража"); return; }
                        var distance = Vector3D.Distance(grid.PositionAndOrientation.Value.Position, consoleAt);
                        if (distance > NearConsole)
                        {
                            Say(sender, "Продать можно только корабль, убранный в гараж не дальше " + NearConsole.ToString("0") +
                                        " м от консоли. «" + name + "» убран в " + (distance / 1000).ToString("0.#") + " км");
                            return;
                        }
                        if (console.Closed || console.MarkedForClose || !console.CubeGrid.IsStatic) return;

                        lock (Lock)
                        {
                            if (ValidListingOf(console) != null) { Say(sender, "На консоли уже продаётся корабль"); return; }
                            if (!File.Exists(file) || !Sellable(sender).Contains(file)) { Say(sender, "Корабля «" + name + "» уже нет в гараже"); return; }
                            var listed = file + ListedSuffix;
                            if (!GarageFiles.Rename(file, listed)) { Say(sender, "Не удалось выставить корабль"); return; }

                            var player = MySession.Static.Players.TryGetIdentity(identity);
                            _listings.Add(new Listing
                            {
                                ShipyardId = console.EntityId, SellerSteamId = sender, SellerIdentity = identity,
                                SellerName = player?.DisplayName ?? sender.ToString(), File = listed, ShipName = name, Price = price,
                                ConsoleAt = console.PositionComp.GetPosition()
                            });
                            Save();
                            Log.Info($"{sender} put {name} up for {price} on trade console {console.EntityId} (put into the garage {distance:0} m from it)");
                            ((IMyProjector)console).SetProjectedGrid(grid);
                            console.Enabled = true;
                        }

                        BroadcastState(console);
                        SendGarage(console, sender, identity);
                    }
                    catch (Exception e)
                    {
                        Log.Error(e, "Putting " + file + " up for sale failed");
                    }
                });
            });
        }

        /// <summary>The main (first) grid of a garage file, or null. Any thread.</summary>
        private static MyObjectBuilder_CubeGrid ReadMainGrid(string file)
        {
            try
            {
                var grid = MyBlueprintUtils.LoadPrefab(file)?.ShipBlueprints?.FirstOrDefault()?.CubeGrids?.FirstOrDefault();
                var cache = file + "B5";
                if (File.Exists(cache)) File.Delete(cache);
                return grid;
            }
            catch (Exception e)
            {
                Log.Error(e, "Reading " + file + " for the trade console failed");
                return null;
            }
        }

        private static void Cancel(MyProjectorBase console, ulong sender, long identity)
        {
            lock (Lock)
            {
                var listing = ValidListingOf(console);
                if (listing == null) return;
                if (identity != console.OwnerId && identity != listing.SellerIdentity) { Say(sender, "Снять с продажи может только продавец"); return; }
                var back = Unlisted(listing.File);
                if (!GarageFiles.Rename(listing.File, back)) { Say(sender, "Не удалось снять корабль с продажи"); return; }
                _listings.Remove(listing);
                Save();
                Log.Info($"{sender} took {listing.ShipName} off trade console {console.EntityId}");
            }

            ((IMyProjector)console).SetProjectedGrid(null);
            BroadcastState(console);
            if (identity == console.OwnerId) SendGarage(console, sender, identity);
        }

        private static void Buy(MyProjectorBase console, ulong sender, long identity)
        {
            lock (Lock)
            {
                var listing = ValidListingOf(console);
                if (listing == null) { Say(sender, "На консоли ничего не продаётся"); return; }
                if (identity == listing.SellerIdentity || sender == listing.SellerSteamId) { Say(sender, "Свой корабль купить нельзя"); return; }
                var sellerFaction = MySession.Static.Factions.GetPlayerFaction(listing.SellerIdentity);
                if (sellerFaction != null && sellerFaction == MySession.Static.Factions.GetPlayerFaction(identity))
                {
                    Say(sender, "Соклановцы не могут покупать корабли друг у друга");
                    return;
                }
                if (!File.Exists(listing.File)) { Say(sender, "Корабль не найден"); return; }
                if (MyBankingSystem.GetBalance(identity) < listing.Price) { Say(sender, "Недостаточно кредитов"); return; }

                var folder = GarageFiles.FolderOf(sender);
                Directory.CreateDirectory(folder);
                var target = GarageFiles.BoughtFile(folder, Path.GetFileName(Unlisted(listing.File)));
                if (!GarageFiles.Rename(listing.File, target)) { Say(sender, "Не удалось передать корабль"); return; }

                // the money in the world: both moves or neither
                if (!MyBankingSystem.ChangeBalance(identity, -listing.Price))
                {
                    GarageFiles.Rename(target, listing.File);
                    Say(sender, "Не удалось списать кредиты");
                    return;
                }

                MyBankingSystem.ChangeBalance(listing.SellerIdentity, listing.Price);
                listing.Pending = true;
                listing.BuyerSteamId = sender;
                listing.BuyerFile = target;
                listing.Operation = GarageFiles.NextOperation();
                Save();
                Log.Info($"{sender} bought {listing.ShipName} from {listing.SellerSteamId} for {listing.Price} on trade console {console.EntityId}: {target}");

                Say(listing.SellerSteamId, "Ваш корабль «" + listing.ShipName + "» куплен за " + listing.Price.ToString("N0") + " SC");
                var number = GarageFiles.List(sender).FindIndex(f => string.Equals(f, target, StringComparison.OrdinalIgnoreCase)) + 1;
                Say(sender, "Корабль куплен и уже в вашем гараже: !g load " + number + " - он появится рядом с вами, бесплатно");
            }

            ((IMyProjector)console).SetProjectedGrid(null);
            BroadcastState(console);
        }

        private static string Unlisted(string listed) =>
            listed.EndsWith(ListedSuffix, StringComparison.Ordinal) ? listed.Substring(0, listed.Length - ListedSuffix.Length) : listed;

        /// <summary>The ship on sale shown on its console (the console is a scaled hologram: it cannot be welded).</summary>
        private static void Project(MyProjectorBase console, string file)
        {
            Task.Run(() =>
            {
                var grid = ReadMainGrid(file);
                if (grid == null) return;
                MyAPIGateway.Utilities.InvokeOnGameThread(() =>
                {
                    if (console.Closed) return;
                    ((IMyProjector)console).SetProjectedGrid(grid);
                    console.Enabled = true;
                });
            });
        }

        private static void Send(ulong to, MarketMessage message)
        {
            MyAPIGateway.Multiplayer.SendMessageTo(Channel, MyAPIGateway.Utilities.SerializeToBinary(message), to);
        }

        private static void Say(ulong to, string text)
        {
            Send(to, new MarketMessage { Type = MarketMessageType.Text, Text = text });
        }

        // ------------------------------------------------------------------ the index on disk

        private static List<Listing> Load()
        {
            try
            {
                if (!File.Exists(IndexPath)) return new List<Listing>();
                using (var stream = File.OpenRead(IndexPath))
                    return (List<Listing>)new XmlSerializer(typeof(List<Listing>)).Deserialize(stream) ?? new List<Listing>();
            }
            catch (Exception e)
            {
                Log.Error(e, "Reading the trade consoles' listings failed");
                return new List<Listing>();
            }
        }

        private static void Save()
        {
            try
            {
                Directory.CreateDirectory(GarageFiles.Root);
                var partial = IndexPath + GarageFiles.Partial;
                using (var stream = File.Create(partial))
                    new XmlSerializer(typeof(List<Listing>)).Serialize(stream, _listings);
                GarageFiles.Rename(partial, IndexPath);
            }
            catch (Exception e)
            {
                Log.Error(e, "Writing the trade consoles' listings failed");
            }
        }
    }
}
