using Microsoft.Extensions.Logging;
using SteamInventoryManager.Common;
using SteamKit2;
using SteamKit2.Discovery;
using SteamKit2.GC;
using SteamKit2.GC.CSGO.Internal;
using SteamKit2.Internal;
using System;
using System.Linq;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using ValveKeyValue;
using ServiceStack.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Text;
using System.Net.Http;
using System.Globalization;
using System.Diagnostics;
using ClosedXML.Report;
using System.Security;
using System.Text.RegularExpressions;
using System.Net;

namespace SteamInventoryManager.Console
{
    public class SteamInventory
    {
        public void Run()
        {
            using ILoggerFactory loggerFactory =
                LoggerFactory.Create(builder =>
                    builder.AddSimpleConsole(options =>
                    {
                        options.IncludeScopes = true;
                        options.SingleLine = true;
                        options.TimestampFormat = "hh:mm:ss ";
                    }));
            logger = loggerFactory.CreateLogger<Program>();
            var today = DateTime.Now.ToString("yyyy/MM/dd");
            WebClient = new DownloadWebClient();
            var kvSerializer = KVSerializer.Create(KVSerializationFormat.KeyValues1Text);
            {
                var result = WebClient.DownloadString(@"https://raw.githubusercontent.com/SteamDatabase/GameTracking-CSGO/master/csgo/resource/csgo_english.txt");
                if (result != null)
                {
#if true
                    result = CSharpCommentRegex.Replace(result, me =>
                    {
                        if (me.Value.StartsWith("/*") || me.Value.StartsWith("//"))
                        {
                            return me.Groups[2].Value;
                        }
                        return me.Value;
                    });
#endif
                    var blacklistedStrings = new String[] {
                            @"\x00A2" ,
                            @"n\Saturday",
                            @"\'allow third party software\'"
                        };
                    foreach (var blacklisted in blacklistedStrings)
                    {
                        result = result.Replace(blacklisted, "");
                    }
                    var vdf = kvSerializer.Deserialize(new MemoryStream(Encoding.UTF8.GetBytes(result ?? "")), new KVSerializerOptions() { HasEscapeSequences = true });
                    csgoResources = vdf;
                    tokens = vdf.Children.Search("Tokens");
                }
            }
            {
                var result = WebClient.DownloadString(@"https://raw.githubusercontent.com/SteamDatabase/GameTracking-CSGO/master/csgo/scripts/items/items_game.txt");
                if (result != null)
                {
                    var vdf = kvSerializer.Deserialize(new MemoryStream(Encoding.UTF8.GetBytes(result ?? "")), new KVSerializerOptions() { HasEscapeSequences = true });
                    csgoItems = vdf;
                }
            }

            {
                var result = WebClient.DownloadString($"https://prices.csgotrader.app/{today}/steam.json");
                if (result != null)
                {
                    steamPrices = JObject.Parse(result);
                }

            }
            { 
                var result = WebClient.DownloadString($"https://prices.csgotrader.app/{today}/buff163.json");
                if (result != null)
                {
                    buff163Prices = JObject.Parse(result);
                }
            }

            //CopyDataFromSteamClientIfExists();
            //System.Console.Write("Please enter Steam Currency Region ([1 = \"USD\",2 = \"GBP\",3 = \"EURO\"]) :");
            //currencyRegion = int.Parse(System.Console.ReadLine());
            System.Console.Write("Please enter Steam Login username :");
            user = System.Console.ReadLine();
            //if(!File.Exists($"sentry.{ user}.bin"))
            {
                ConsoleKeyInfo i;
                System.Console.Write("Please enter Steam Login password :");
                do
                {
                    i = System.Console.ReadKey(true);

                    if (i.Key == ConsoleKey.Backspace)
                    {
                        if (pass.Length > 0)
                        {
                            pass = pass.Substring(0, pass.Length - 1);
                            System.Console.Write("\b \b");
                        }
                    }
                    else if (i.KeyChar != '\u0000') // KeyChar == '\u0000' if the key pressed does not correspond to a printable character, e.g. F1, Pause-Break, etc
                    {
                        pass += (i.KeyChar);
                        System.Console.Write("*");
                    }

                    // Exit if Enter key is pressed.
                } while (i.Key != ConsoleKey.Enter);
                pass = pass.Substring(0, (pass.Length - 1)); // remove \r
                System.Console.Write("\n");
            }

            cellId = 0u;
            inventory = new Dictionary<ulong, CSGOItem>();
            storageUnits = new Dictionary<ulong, HashSet<ulong>>();
            // if we've previously connected and saved our cellid, load it.
            if (File.Exists("cellid.txt"))
            {
                if (!uint.TryParse(File.ReadAllText("cellid.txt"), out cellId))
                {
                    logger.LogInformation("Error parsing cellid from cellid.txt. Continuing with cellid 0.");
                    cellId = 0;
                }
                else
                {
                    logger.LogInformation($"Using persisted cell ID {cellId}");
                }
            }

            var configuration = SteamConfiguration.Create(b =>
               b.WithCellID(cellId)
                .WithServerListProvider(new FileStorageServerListProvider("servers_list.bin")));
            // create our steamclient instance
            steamClient = new SteamClient(configuration);
            // create the callback manager which will route callbacks to function calls
            manager = new CallbackManager(steamClient);
            gameCoordinator = steamClient.GetHandler<SteamGameCoordinator>();
            manager.Subscribe<SteamGameCoordinator.MessageCallback>(OnGcMessage);
            // get the steamuser handler, which is used for logging on after successfully connecting
            steamUser = steamClient.GetHandler<SteamUser>();


            // register a few callbacks we're interested in
            // these are registered upon creation to a callback manager, which will then route the callbacks
            // to the functions specified
            manager.Subscribe<SteamClient.ConnectedCallback>(OnConnected);
            manager.Subscribe<SteamClient.DisconnectedCallback>(OnDisconnected);
            manager.Subscribe<SteamUser.LoginKeyCallback>(OnNewLoginKey);
            manager.Subscribe<SteamUser.LoggedOnCallback>(OnLoggedOn);
            manager.Subscribe<SteamUser.LoggedOffCallback>(OnLoggedOff);

            // this callback is triggered when the steam servers wish for the client to store the sentry file
            manager.Subscribe<SteamUser.UpdateMachineAuthCallback>(OnMachineAuth);
            System.Console.CancelKeyPress += (s, e) =>
            {
                e.Cancel = true;
                logger.LogInformation("Received {0}, disconnecting...", e.SpecialKey);
                steamUser.LogOff();
                doReconnect = false;
                logger.LogInformation("Please wait outputting informations...");
                jsonOutput = new JsonOutput();
                jsonOutput.inventory = inventory.Values.Where(x => x.StorageUnitId == 0).ToArray();
                jsonOutput.storageUnits = storageUnits.ToDictionary(x => inventory[x.Key].CustomName, x => x.Value.Select(x => inventory[x]).ToArray());
                logger.LogInformation("Please wait valuating items...");
                ValuateInventory();
                logger.LogInformation("Please wait writing files...");
                File.WriteAllText("inventory.json", JsonConvert.SerializeObject(inventory, Formatting.Indented));
                logger.LogInformation("Wrote to inventory.json");
                File.WriteAllText("storage_units.json", JsonConvert.SerializeObject(storageUnits, Formatting.Indented));
                logger.LogInformation("Wrote to storage_units.json");
                File.WriteAllText("output.json", JsonConvert.SerializeObject(jsonOutput, Formatting.Indented));
                logger.LogInformation("Wrote to output.json");
                foreach (var unit in jsonOutput.storageUnits)
                {
                    var excel = new ExcelOutput();
                    excel.name = unit.Key;
                    excel.username = user;
                    excel.CSGOitems = unit.Value.Select(x => x.ToOutputCSGOItem()).ToList();
                    var template = new XLTemplate(@".\reports\StorageUnitTemplate.xlsx");
                    template.AddVariable(excel);
                    template.Generate();
                    template.SaveAs($"{excel.name} Report.xlsx");
                    template.Dispose();
                    logger.LogInformation($"Wrote to {excel.name} Report.xlsx");

                }
                {
                    var excel = new ExcelOutput();
                    excel.username = user;
                    excel.name = "Inventory";
                    excel.CSGOitems = inventory.Values.Where(x => x.StorageUnitId == 0).Select(x => x.ToOutputCSGOItem()).ToList();
                    var template = new XLTemplate(@".\reports\StorageUnitTemplate.xlsx");
                    template.AddVariable(excel);
                    template.Generate();
                    template.SaveAs($"{excel.name} Report.xlsx");
                    template.Dispose();
                    logger.LogInformation($"Wrote to {excel.name} Report.xlsx");
                }
                isRunning = false;
                e.Cancel = false;
                Process.GetCurrentProcess().Kill();
            };
            isRunning = true;
            isLoggedIn = false;
            logger.LogInformation("Connecting to Steam...");
            var serverRecord = ServerRecord.CreateWebSocketServer("localhost:8888");
            // initiate the connection
            steamClient.Connect();
            // create our callback handling loop
            while (isRunning)
            {
                // in order for the callbacks to get routed, they need to be handled by the manager
                if (isRunning) manager.RunWaitCallbacks(TimeSpan.FromSeconds(1));
            }
        }
        WebClient WebClient;
        private void ValuateInventory()
        {
            try
            {
                foreach (var item in inventory.Values)
                {
                    if (!item.IsStorageUnit)
                    {
                        {
                            var steamvalue = 0.0m;
                            var buff163value = 0.0m;
                            if (steamPrices[item.marketName] != null)
                            {
                                if(decimal.TryParse((string)(steamPrices[item.marketName]["last_7d"]),out var res))
                                {
                                    steamvalue = res;
                                }
                            }
                            if (buff163Prices[item.marketName] != null)
                            {
                                if (decimal.TryParse((string)(buff163Prices[item.marketName]["starting_at"]["price"]), out var res))
                                {
                                    buff163value = res;
                                }
                            }
                            item.steamValue = steamvalue;
                            item.buff163Value = buff163value;
                            logger.LogInformation($"Price of {item.marketName} : {item.steamValue} (steam) {item.buff163Value} (buff163)");
                        
                        }
                    }
                }

                foreach (var unit in storageUnits)
                {
                    var unitId = unit.Key;
                    var unitItemsId = unit.Value.ToArray();
                    inventory[unitId].steamValue = unitItemsId.Sum(x => inventory[x].steamValue);
                    inventory[unitId].buff163Value = unitItemsId.Sum(x => inventory[x].buff163Value);
                }
            }
            catch (Exception ex)
            {
                logger.LogInformation(ex.Message);
                throw;
            }

        }

        const string LoginKeyFileName = "loginkey.txt";

        void SaveLoginKey(string loginKey) => File.WriteAllText(LoginKeyFileName, loginKey);

        void CopyDataFromSteamClientIfExists()
        {
            string VdfFile = @"C:\Program Files (x86)\Steam\config\config.vdf";
            if (File.Exists(VdfFile))
            {
                var configData = KeyValue.LoadFromString(File.ReadAllText(VdfFile));
                var config = configData["Software"]["Valve"]["Steam"];
                var sentryFileLocation = config["SentryFile"].Value;
                var cellId = config["CurrentCellID"];
                File.WriteAllText("cellid.txt", cellId.ToString());
                var users = config["Accounts"].Children;
                foreach (var userdata in users)
                {
                    var user = (userdata).Name;

                    File.Copy(sentryFileLocation, $"sentry.{user}.bin", true);
                }
            }
        }

        string ReadLoginKey()
        {
            try
            {
                return File.ReadAllText(LoginKeyFileName);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
        }
        void OnNewLoginKey(SteamUser.LoginKeyCallback cb)
        {
            SaveLoginKey(cb.LoginKey);
            logger.LogInformation("Got new login key.");
            steamClient.GetHandler<SteamUser>().AcceptNewLoginKey(cb);
        }

        void OnNotificationGetStorageUnitContents(IPacketGCMsg packetMsg)
        {
            var msg = new ClientGCMsgProtobuf<CMsgGCItemCustomizationNotification>(packetMsg);
            if (msg.MsgType == (uint)EGCItemCustomizationNotification.k_EGCItemCustomizationNotification_CasketContents)
            {
            }
        }

        void OnItemAdded(IPacketGCMsg packetMsg)
        {
            var msg = new ClientGCMsgProtobuf<CMsgSOSingleObject>(packetMsg);
            if (msg.Body.type_id == 1)
            {
                //Is an item

                var stream = new MemoryStream(msg.Body.object_data);
                var item = ProtoBuf.Serializer.Deserialize<CSOEconItem>(stream);
                var csgoItem = ProcessEconomyItem(item);
                UpdateInventory(csgoItem);
                UpdateStorageUnits();
            }
        }



        void OnGcMessage(SteamGameCoordinator.MessageCallback callback)
        {
            // setup our dispatch table for messages
            // this makes the code cleaner and easier to maintain
            var messageMap = new Dictionary<uint, Action<IPacketGCMsg>>
            {
                { (uint) ESOMsg.k_ESOMsg_Create , OnItemAdded },
                { (uint) ECsgoGCMsg.k_EMsgGCCStrike15_v2_GC2ClientGlobalStats , OnReady},
                { (uint) EGCItemCustomizationNotification.k_EGCItemCustomizationNotification_CasketContents, OnNotificationGetStorageUnitContents},
                { ( uint )EGCBaseClientMsg.k_EMsgGCClientWelcome, OnClientWelcome },
                { (uint )EGCItemMsg.k_EMsgGCItemCustomizationNotification ,OnReady },
            };
            logger.LogInformation("Current GC Msg Code:" + callback.EMsg);
            Action<IPacketGCMsg> func;
            if (!messageMap.TryGetValue(callback.EMsg, out func))
            {
                // this will happen when we recieve some GC messages that we're not handling
                // this is okay because we're handling every essential message, and the rest can be ignored
                return;
            }

            func(callback.Message);
        }

        void OnReady(IPacketGCMsg obj)
        {
            logger.LogInformation($"CSGO Client Items Retrieved");
        }

        // this message arrives when the GC welcomes a client
        // this happens after telling steam that we launched csgo (with the ClientGamesPlayed message)
        // this can also happen after the GC has restarted (due to a crash or new version)
        void OnClientWelcome(IPacketGCMsg packetMsg)
        {
            // in order to get at the contents of the message, we need to create a ClientGCMsgProtobuf from the packet message we recieve
            // note here the difference between ClientGCMsgProtobuf and the ClientMsgProtobuf used when sending ClientGamesPlayed
            // this message is used for the GC, while the other is used for general steam messages
            var msg = new ClientGCMsgProtobuf<CMsgClientWelcome>(packetMsg);

            logger.LogInformation("GC is welcoming us. Version: {0}", msg.Body.version);

            if (msg.Body.outofdate_subscribed_caches != null)
            {
                if (msg.Body.outofdate_subscribed_caches.Count > 0)
                {
                    var prevStorageUnitCount = storageUnits.Count;
                    foreach (var cache in msg.Body.outofdate_subscribed_caches[0].objects)
                    {
                        switch (cache.type_id)
                        {
                            case 1:
                                //Inventory
                                foreach (var obj in cache.object_data)
                                {
                                    var stream = new MemoryStream(obj);
                                    var item = ProtoBuf.Serializer.Deserialize<CSOEconItem>(stream);
                                    var csgoItem = ProcessEconomyItem(item);
                                    UpdateInventory(csgoItem);
                                }

                                UpdateStorageUnits();
                                break;
                            default:
                                break;
                        }
                    }
                }
            }

        }

        private void UpdateInventory(CSGOItem csgoItem)
        {
            if (!inventory.ContainsKey(csgoItem.Id))
            {
                inventory.Add(csgoItem.Id, csgoItem);
            }
            if (csgoItem.IsStorageUnit)
            {
                var itemsInStorageUnit = new HashSet<ulong>();
                if (!storageUnits.ContainsKey(csgoItem.Id))
                {
                    storageUnits.Add(csgoItem.Id, itemsInStorageUnit);
                    var casketId = csgoItem.Id;
                    var casketMessage = new ClientGCMsgProtobuf<CMsgCasketItem>((uint)EGCItemMsg.k_EMsgGCCasketItemLoadContents);
                    casketMessage.Body.casket_item_id = casketId;
                    casketMessage.Body.item_item_id = casketId;
                    gameCoordinator.Send(casketMessage, APPID_CSGO);
                }
            }
        }

        private void UpdateStorageUnits()
        {
            foreach (var csgoItem in inventory.Values)
            {
                if (!csgoItem.IsStorageUnit && csgoItem.StorageUnitId != 0)
                {
                    var hasUnit = storageUnits.ContainsKey(csgoItem.StorageUnitId);
                    if (hasUnit)
                    {
                        var unitItems = storageUnits[csgoItem.StorageUnitId];
                        if (!unitItems.Contains(csgoItem.Id))
                        {
                            unitItems.Add(csgoItem.Id);
                        }
                        storageUnits[csgoItem.StorageUnitId] = unitItems;
                    }
                }
            }
        }



        private CSGOItem ProcessEconomyItem(CSOEconItem item)
        {

            var csgoItem = new CSGOItem();
            csgoItem.StatTrakType = StatTrackType.None;
            csgoItem.Id = item.id;
            csgoItem.IsStorageUnit = false;
            csgoItem.IsNew = ((item.inventory >> 30) & 1) == 0 ? false : true;
            csgoItem.Position = (csgoItem.IsNew ? 0 : item.inventory & 0xFFFF);
            var Stickers = new List<Sticker>();
            var casketIdLow = getAttributeValueBytes(272);
            var casketIdHigh = getAttributeValueBytes(273);
            if (casketIdLow != null && casketIdHigh != null)
            {
                var casketIdLowBit = BitConverter.ToUInt32(casketIdLow);
                var casketIdHighBit = BitConverter.ToUInt32(casketIdHigh);
                csgoItem.StorageUnitId = (ulong)casketIdHighBit << 32 | (ulong)casketIdLowBit;
            }
            var customNameBytes = getAttributeValueBytes(111);
            if (customNameBytes != null && item.custom_name != null)
            {
                csgoItem.CustomName = Encoding.UTF8.GetString(customNameBytes.AsSpan().Slice(2).ToArray());
            }
            var customDescriptionBytes = getAttributeValueBytes(112);
            if (customDescriptionBytes != null && item.custom_desc != null)
            {
                csgoItem.CustomDescription = Encoding.UTF8.GetString(customDescriptionBytes.AsSpan().Slice(2).ToArray());
            }
            var paintIndexBytes = getAttributeValueBytes(6);
            if (paintIndexBytes != null)
            {
                var paintIndex = (uint)(BitConverter.ToSingle(paintIndexBytes));
                var paintkitDef = csgoItems.Children.Search("paint_kits").Search(paintIndex.ToString());
                var paintkitNameTag = ((string)paintkitDef["description_tag"]).Substring(1);
                var paintkitName = GetTokensValue(paintkitNameTag);
                while (paintkitName.Contains('<'))
                {
                    var startIdx = paintkitName.IndexOf('<');
                    var count = paintkitName.IndexOf('>') - startIdx + 1;
                    paintkitName = paintkitName.Remove(startIdx, count);
                }
                var paintkitDescriptionTag = ((string)paintkitDef["description_string"]).Substring(1);
                var paintkitDescription = GetTokensValue(paintkitDescriptionTag);
                while (paintkitDescription.Contains('<'))
                {
                    var startIdx = paintkitDescription.IndexOf('<');
                    var count = paintkitDescription.IndexOf('>') - startIdx + 1;
                    paintkitDescription = paintkitDescription.Remove(startIdx, count);
                }
                csgoItem.PaintName = paintkitName;
                csgoItem.PaintDescription = paintkitDescription;
                var paintSeedBytes = getAttributeValueBytes(7);
                if (paintSeedBytes != null)
                {
                    csgoItem.PaintSeed = (float)Math.Floor(BitConverter.ToSingle(paintSeedBytes));
                }

                var paintWearBytes = getAttributeValueBytes(8);
                if (paintWearBytes != null)
                {
                    csgoItem.PaintWear = BitConverter.ToSingle(paintWearBytes);
                }
            }


            var killCountBytes = getAttributeValueBytes(80);
            if (killCountBytes != null)
            {
                csgoItem.StatTrackCount = BitConverter.ToUInt32(killCountBytes);
            }
            var musicIdBytes = getAttributeValueBytes(166);
            if (musicIdBytes != null)
            {
                csgoItem.IsMusicKit = true;
                var musicKitId = BitConverter.ToUInt32(musicIdBytes);
                var musickitDef = csgoItems.Children.Search("music_definitions")[musicKitId.ToString()];
                var musickitNameTag = ((string)musickitDef["loc_name"]).Substring(1);
                var musickitName = GetTokensValue(musickitNameTag);
                while (musickitName.Contains('<'))
                {
                    var startIdx = musickitName.IndexOf('<');
                    var count = musickitName.IndexOf('>') - startIdx + 1;
                    musickitName = musickitName.Remove(startIdx, count);
                }
                var musickitDescriptionTag = ((string)musickitDef["loc_description"]).Substring(1);
                var musickitDescription = GetTokensValue(musickitDescriptionTag);
                while (musickitDescription.Contains('<'))
                {
                    var startIdx = musickitDescription.IndexOf('<');
                    var count = musickitDescription.IndexOf('>') - startIdx + 1;
                    musickitDescription = musickitDescription.Remove(startIdx, count);
                }
                csgoItem.MusickitName = musickitName;
                csgoItem.MusickitDescription = musickitDescription;
            }
            var killCountTypeBytes = getAttributeValueBytes(81);
            if (killCountTypeBytes != null)
            {
                csgoItem.StatTrakType = (StatTrackType)BitConverter.ToUInt32(killCountTypeBytes);
            }

            var tradableAfterDateBytes = getAttributeValueBytes(75);
            if (tradableAfterDateBytes != null)
            {
                var seconds = BitConverter.ToUInt32(tradableAfterDateBytes);
                DateTime date = new DateTime(1970, 1, 1, 0, 0, 0, 0, DateTimeKind.Utc);
                date = date.AddSeconds(seconds);
                csgoItem.TradableAfter = date;
            }

            var sprayTintIdBytes = getAttributeValueBytes(233);
            if (sprayTintIdBytes != null)
            {
                var sprayTintId = BitConverter.ToUInt32(sprayTintIdBytes);
                var sprayTintName = GetTokensValue($"Attrib_SprayTintValue_{sprayTintId}");
                csgoItem.SprayName = sprayTintName;
            }
            //stickers code ref: (https://github.com/DoctorMcKay/node-globaloffensive/blob/2b3ad4a678034c472096fa198af9424695e1c4ca/handlers.js#L167)
            for (int i = 0; i <= 5; i++)
            {
                var sticker = new Sticker();
                var stickerIdBytes = getAttributeValueBytes(113 + (i * 4) + 0);
                var stickerWearBytes = getAttributeValueBytes(113 + (i * 4) + 1);
                var stickerScaleBytes = getAttributeValueBytes(113 + (i * 4) + 2);
                var stickerRotationBytes = getAttributeValueBytes(113 + (i * 4) + 3);
                if (stickerIdBytes != null)
                {
                    sticker.Slot = i;
                    sticker.Sticker_id = BitConverter.ToUInt32(stickerIdBytes);
                    {
                        var stickerDef = csgoItems.Children.Search("sticker_kits")[sticker.Sticker_id.ToString()];
                        var stickerNameTag = ((string)stickerDef["item_name"]).Substring(1);
                        var stickerName = GetTokensValue(stickerNameTag);
                        while (stickerName.Contains('<'))
                        {
                            var startIdx = stickerName.IndexOf('<');
                            var count = stickerName.IndexOf('>') - startIdx + 1;
                            stickerName = stickerName.Remove(startIdx, count);
                        }
                        var stickerDescriptionTag = ((string)stickerDef["description_string"]).Substring(1);
                        var stickerDescription = GetTokensValue(stickerDescriptionTag);
                        while (stickerDescription.Contains('<'))
                        {
                            var startIdx = stickerDescriptionTag.IndexOf('<');
                            var count = stickerDescriptionTag.IndexOf('>') - startIdx + 1;
                            stickerDescription = stickerDescriptionTag.Remove(startIdx, count);
                        }
                        sticker.Name = stickerName;
                        sticker.Description = stickerDescription;
                    }
                    if (stickerWearBytes != null)
                    {
                        sticker.Wear = BitConverter.ToSingle(stickerWearBytes);
                    }
                    if (stickerScaleBytes != null)
                    {
                        sticker.scale = BitConverter.ToSingle(stickerScaleBytes);
                    }
                    if (stickerRotationBytes != null)
                    {
                        sticker.rotation = BitConverter.ToSingle(stickerRotationBytes);
                    }
                    Stickers.Add(sticker);
                }

            }
            csgoItem.Stickers = Stickers.ToArray();
            var itemDef = csgoItems.Search("items")[item.def_index.ToString()];
            {

                if (itemDef["item_name"] != null)
                {
                    var itemNameTag = ((string)itemDef["item_name"]).Substring(1);
                    var itemName = GetTokensValue(itemNameTag);
                    while (itemName.Contains('<'))
                    {
                        var startIdx = itemName.IndexOf('<');
                        var count = itemName.IndexOf('>') - startIdx + 1;
                        itemName = itemName.Remove(startIdx, count);
                    }
                    csgoItem.ItemName = itemName;

                }
                if (itemDef["item_description"] != null)
                {
                    var itemDescriptionTag = ((string)itemDef["item_description"]).Substring(1);
                    var itemDescription = GetTokensValue(itemDescriptionTag);
                    while (itemDescription.Contains('<'))
                    {
                        var startIdx = itemDescription.IndexOf('<');
                        var count = itemDescription.IndexOf('>') - startIdx + 1;
                        itemDescription = itemDescription.Remove(startIdx, count);
                    }
                    csgoItem.ItemDescription = itemDescription;
                }

            }
            if (csgoItem.ItemName == null)
            {
                if (itemDef["prefab"] != null)
                {
                    var prefabDef = csgoItems.Children.Search("prefabs")[(string)itemDef["prefab"]];
                    if (prefabDef["item_name"] != null)
                    {
                        var itemName = GetTokensValue(((string)prefabDef["item_name"]).Substring(1));
                        while (itemName.Contains('<'))
                        {
                            var startIdx = itemName.IndexOf('<');
                            var count = itemName.IndexOf('>') - startIdx + 1;
                            itemName = itemName.Remove(startIdx, count);
                        }
                        csgoItem.ItemName = itemName;
                    }

                }


            }
            if (csgoItem.ItemDescription == null)
            {
                if (itemDef["prefab"] != null)
                {
                    var prefabDef = csgoItems.Children.Search("prefabs")[(string)itemDef["prefab"]];
                    if (prefabDef["item_description"] != null)
                    {
                        var itemDescription = GetTokensValue(((string)prefabDef["item_description"]).Substring(1));
                        while (itemDescription.Contains('<'))
                        {
                            var startIdx = itemDescription.IndexOf('<');
                            var count = itemDescription.IndexOf('>') - startIdx + 1;
                            itemDescription = itemDescription.Remove(startIdx, count);
                        }
                        csgoItem.ItemDescription = itemDescription;
                    }

                }
            }
            if (csgoItem.ItemName.Contains("Case", StringComparison.OrdinalIgnoreCase))
            {
                if (csgoItem.ItemDescription == null && item.def_index == 4001 && item.attribute.Count == 0)
                {
                    csgoItem.ItemName = "Fake CSGO Case";
                }
            }
            // def_index-specific attribute parsing
            switch (item.def_index)
            {
                case 1348:
                case 1349:
                    // Grafitti
                    csgoItem.IsGrafitti = true;
                    break;
                case 58:
                    // Music Kit
                    csgoItem.IsMusicKit = true;
                    break;
                case 1201:
                    // Storage Unit
                    csgoItem.IsStorageUnit = true;
                    var itemCountBytes = getAttributeValueBytes(270);
                    if (itemCountBytes != null)
                    {
                        csgoItem.StorageUnitContainedItemCount = BitConverter.ToUInt32(itemCountBytes);
                    }
                    break;
            }

            byte[] getAttributeValueBytes(int attribDefIndex)
            {
                var attrib = (item.attribute).Find(attrib => attrib.def_index == attribDefIndex);
                return attrib == null ? null : attrib.value_bytes;
            }
            return csgoItem;
        }

        void OnConnected(SteamClient.ConnectedCallback callback)
        {
            logger.LogInformation("Connected to Steam! Logging in '{0}'...", user);

            byte[] sentryHash = null;
            if (File.Exists($"sentry.{user}.bin"))
            {
                // if we have a saved sentry file, read and sha-1 hash it
                byte[] sentryFile = File.ReadAllBytes($"sentry.{user}.bin");
                sentryHash = CryptoHelper.SHAHash(sentryFile);
            }

            steamUser.LogOn(new SteamUser.LogOnDetails
            {
                Username = user,
                ShouldRememberPassword = true,
                Password = pass,
                CellID = (cellId),
                LoginKey = ReadLoginKey(),
                // in this sample, we pass in an additional authcode
                // this value will be null (which is the default) for our first logon attempt
                AuthCode = authCode,

                // if the account is using 2-factor auth, we'll provide the two factor code instead
                // this will also be null on our first logon attempt
                TwoFactorCode = twoFactorAuth,

                // our subsequent logons use the hash of the sentry file as proof of ownership of the file
                // this will also be null for our first (no authcode) and second (authcode only) logon attempts
                SentryFileHash = sentryHash
            });
        }

        void OnDisconnected(SteamClient.DisconnectedCallback callback)
        {
            // after recieving an AccountLogonDenied, we'll be disconnected from steam
            // so after we read an authcode from the user, we need to reconnect to begin the logon flow again

            logger.LogInformation("Disconnected from Steam, reconnecting in 5...");

            Thread.Sleep(TimeSpan.FromSeconds(5));

            if (isRunning && doReconnect) steamClient.Connect();
        }

        void OnLoggedOn(SteamUser.LoggedOnCallback callback)
        {
            bool isSteamGuard = callback.Result == EResult.AccountLogonDenied;
            bool is2FA = callback.Result == EResult.AccountLoginDeniedNeedTwoFactor;

            if (isSteamGuard || is2FA)
            {
                System.Console.WriteLine("This account is SteamGuard protected! ");

                if (is2FA)
                {
                    System.Console.Write("Please enter your 2 factor auth code from your authenticator app: ");
                    twoFactorAuth = System.Console.ReadLine();
                }
                else
                {
                    System.Console.Write("Please enter the auth code sent to the email at {0}: ", callback.EmailDomain);
                    authCode = System.Console.ReadLine();
                }

                return;
            }

            if (callback.Result != EResult.OK)
            {
                logger.LogInformation("Unable to logon to Steam: {0} / {1}", callback.Result, callback.ExtendedResult);
                File.Delete($"sentry.{user}.bin");
                File.Delete(LoginKeyFileName);
                isRunning = false;
                return;
            }
            // save the current cellid somewhere. if we lose our saved server list, we can use this when retrieving
            // servers from the Steam Directory.
            File.WriteAllText("cellid.txt", callback.CellID.ToString());
            logger.LogInformation("Successfully logged on!");

            // at this point, we'd be able to perform actions on Steam
            // steamkit doesn't expose the "play game" message through any handler, so we'll just send the message manually
            var playGame = new ClientMsgProtobuf<CMsgClientGamesPlayed>(EMsg.ClientGamesPlayed);

            playGame.Body.games_played.Add(new CMsgClientGamesPlayed.GamePlayed
            {
                game_id = new GameID(APPID_CSGO), // or game_id = APPID,
            });

            // send it off
            // notice here we're sending this message directly using the SteamClient
            steamClient.Send(playGame);


            // delay a little to give steam some time to establish a GC connection to us
            Thread.Sleep(5000);

            // inform the csgo GC
            var clientHello = new ClientGCMsgProtobuf<CMsgClientHello>((uint)EGCBaseClientMsg.k_EMsgGCClientHello);
            gameCoordinator.Send(clientHello, APPID_CSGO);
            Thread.Sleep(5000);
            isLoggedIn = true;
        }

        void OnLoggedOff(SteamUser.LoggedOffCallback callback)
        {
            logger.LogInformation("Logged off of Steam: {0}", callback.Result);
        }

        void OnMachineAuth(SteamUser.UpdateMachineAuthCallback callback)
        {
            logger.LogInformation("Updating sentryfile...");
            // write out our sentry file
            // ideally we'd want to write to the filename specified in the callback
            // but then this sample would require more code to find the correct sentry file to read during logon
            // for the sake of simplicity, we'll just use "sentry.bin"
            int fileSize;
            byte[] sentryHash;
            using (var fs = File.Open($"sentry.{user}.bin", FileMode.OpenOrCreate, FileAccess.ReadWrite))
            {
                fs.Seek(callback.Offset, SeekOrigin.Begin);
                fs.Write(callback.Data, 0, callback.BytesToWrite);
                fileSize = (int)fs.Length;

                fs.Seek(0, SeekOrigin.Begin);
                using (var sha = SHA1.Create())
                {
                    sentryHash = sha.ComputeHash(fs);
                }
            }

            // inform the steam servers that we're accepting this sentry file
            steamUser.SendMachineAuthResponse(new SteamUser.MachineAuthDetails
            {
                JobID = callback.JobID,

                FileName = callback.FileName,

                BytesWritten = callback.BytesToWrite,
                FileSize = fileSize,
                Offset = callback.Offset,

                Result = EResult.OK,
                LastError = 0,

                OneTimePassword = callback.OneTimePassword,

                SentryFileHash = sentryHash,
            });

            logger.LogInformation("Done!");
        }

        private string GetTokensValue(string value)
        {
            var result = tokens.Where(x => x.Name.ToUpper().Trim() == value.ToUpper().Trim()).First();
            return result == null ? null : result.Value.ToString();
        }

        // code from http://stackoverflow.com/questions/3524317/regex-to-strip-line-comments-from-c-sharp/3524689#3524689
        // 4 combination of patterns:
        // - block comments (//...)
        // - line comments (/*...*/)
        // - string ("...")
        // - literal (@"...")
        // new-line character(\r?\n or end-of-line) will be captured by group 2.
        private static Regex CSharpCommentRegex =
            new Regex(@"(\/\/.*?(\r?\n|$))|(\/\*(?:[\s\S]*?)\*\/)|(""(?:\\[^\n]|[^""\n])*"")|(@(?:""[^""]*"")+)", RegexOptions.Compiled);
        SteamClient steamClient;
        CallbackManager manager;
        SteamUser steamUser;
        SteamGameCoordinator gameCoordinator;
        Dictionary<ulong, CSGOItem> inventory;
        Dictionary<ulong, HashSet<ulong>> storageUnits;
        JObject buff163Prices;
        JObject steamPrices;
        JsonOutput jsonOutput;
        volatile bool isRunning;
        volatile bool doReconnect = true;
        bool isLoggedIn;
        string user;
        string pass;
        uint cellId;
        string authCode, twoFactorAuth;
        public ILogger<Program> logger;
        int currencyRegion = 1;
        KVObject csgoResources;
        KVObject tokens;
        KVObject csgoItems;
        const int APPID_DOTA2 = 570;
        const int APPID_TF2 = 440;
        const int APPID_CSGO = 730;
    }

    public class JsonOutput
    {
        public CSGOItem[] inventory;
        public Dictionary<string, CSGOItem[]> storageUnits;
    }

    public class ExcelOutput
    {
        public string username { get; set; }
        public string name { get; set; }
        public List<OutputCSGOItem> CSGOitems { get; set; }
    }

    public static class UtilExtensions
    {
        public static KVObject Search(this IEnumerable<KVObject> obj, string value)
        {
            var objs = obj.Where(x => x.Name.ToUpper().Trim() == value.ToUpper().Trim()).SelectMany(x => x.Children);
            var result = new KVObject(value,objs);
            return result;
        }

    }

    class DownloadWebClient : WebClient
    {
        protected override WebRequest GetWebRequest(Uri address)
        {
            HttpWebRequest request = base.GetWebRequest(address) as HttpWebRequest;
            request.AutomaticDecompression = DecompressionMethods.All;
            return request;
        }
    }
}

