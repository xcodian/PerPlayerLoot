﻿using System;
using System.Collections.Generic;
using System.Text;

using Terraria;
using TShockAPI;
using TerrariaApi.Server;
using System.IO;
using System.IO.Streams;

using Newtonsoft.Json;
using Newtonsoft.Json.Bson;
using System.Runtime.Serialization.Formatters.Binary;
using Microsoft.Data.Sqlite;

namespace PerPlayerLoot
{
    // barebones class representing item data which can be deserialized from json
    public class JItem
    {
        public int id { get; set; }
        public int stack { get; set; }
        public byte prefix { get; set; }
    }


    // database of FakeChest's
    public class FakeChestDatabase
    {
        // Map { UUID: { ChestID: Chest } }
        public static Dictionary<string, Dictionary<int, Chest>> fakeChestsMap = new Dictionary<string, Dictionary<int, Chest>> { };

        public static HashSet<(int, int)> playerPlacedChests = new HashSet<(int, int)>(); // tile x, y of player placed chests

        private static string connString = "Data Source=tshock/perplayerloot.sqlite";

        public FakeChestDatabase() { }

        public void Initialize()
        {
            CreateTables();
            LoadFakeChests();
        }

        public void CreateTables()
        {
            TSPlayer.Server.SendInfoMessage("Setting up per-player chests database...");
            using (SqliteConnection conn = new SqliteConnection(connString))
            {
                conn.Open();

                string sql = @"
                    CREATE TABLE IF NOT EXISTS chests (
                        id INTEGER NOT NULL,
                        playerUuid TEXT NOT NULL,
                        x INTEGER NOT NULL,
                        y INTEGER NOT NULL,
                        items BLOB NOT NULL,
                        PRIMARY KEY (id, playerUuid)
                    );

                    CREATE TABLE IF NOT EXISTS placed (
                        x INTEGER NOT NULL,
                        y INTEGER NOT NULL,
                        PRIMARY KEY (x, y)
                    );
                ";

                using (var cmd = new SqliteCommand(sql, conn)) 
                    cmd.ExecuteNonQuery();

            }
        }

        public void LoadFakeChests()
        {
            TSPlayer.Server.SendInfoMessage("Loading per-player loot chest inventories...");
            int count = 0;

            using (SqliteConnection conn = new SqliteConnection(connString))
            {
                conn.Open();

                // load loot chests
                using (var cmd = new SqliteCommand("SELECT id, playerUuid, x, y, items FROM chests;", conn))
                {
                    SqliteDataReader reader = cmd.ExecuteReader();

                    while (reader.Read())
                    {
                        string playerUuid = Convert.ToString(reader["playerUuid"]);
                        int chestId = Convert.ToInt32(reader["id"]);

                        // get the items list
                        List<Item> items = new List<Item>();

                        // read blob from column
                        MemoryStream itemsRaw = new MemoryStream((byte[]) reader["items"]);
                        // deserialize with bson
                        using (var br = new BsonReader(itemsRaw))
                        {
                            br.ReadRootValueAsArray = true;
                            
                            // do the actual deserialization
                            var jItems = (new JsonSerializer()).Deserialize<IList<JItem>>(br);

                            // convert each JItem to a real Item
                            foreach (var jItem in jItems)
                            {
                                if (jItem == null)
                                {
                                    items.Add(null);
                                    continue;
                                }

                                var item = new Item();
                                item.netDefaults(jItem.id);
                                item.stack = jItem.stack;
                                item.prefix = jItem.prefix;

                                items.Add(item);
                            }
                        }

                        Chest chest = new Chest(); // construct a terraria chest
                        chest.x = Convert.ToInt32(reader["x"]);
                        chest.y = Convert.ToInt32(reader["y"]);
                        chest.item = items.ToArray();

                        // save it in the fake chest map
                        var playerChests = fakeChestsMap.GetValueOrDefault(playerUuid, new Dictionary<int, Chest>());
                        fakeChestsMap[playerUuid] = playerChests;

                        fakeChestsMap[playerUuid][chestId] = chest;

                        count++;
                    }
                }

                // load tile exclusions
                using (var cmd = new SqliteCommand("SELECT x, y FROM placed;", conn))
                {
                    SqliteDataReader reader = cmd.ExecuteReader();

                    playerPlacedChests.Clear();

                    while (reader.Read())
                    {
                        int x = Convert.ToInt32(reader["x"]);
                        int y = Convert.ToInt32(reader["y"]);

                        playerPlacedChests.Add((x, y));
                    }
                }
            }

            // TSPlayer.Server.SendSuccessMessage($"Loaded {count} loot chest inventories, {playerPlacedChests.Count} player-placed chests.");
            //I think it's too frequent, I suggest commenting out.
        }

        public void SaveFakeChests(string? PlayerUuid = null, int? ChestId = null)
        {
            //TSPlayer.Server.SendInfoMessage("Saving per-player loot chest inventories...");
            //I think it's too frequent, I suggest commenting out.
            int count = 0;

            using (SqliteConnection conn = new SqliteConnection(connString))
            {
                conn.Open();

                foreach (KeyValuePair<string, Dictionary<int, Chest>> playerEntry in fakeChestsMap)
                {
                    string playerUuid = playerEntry.Key;
                    // If a player UUID is specified and the current player does not match, then skip
                    if (PlayerUuid != null && playerUuid != PlayerUuid)
                    {
                        continue;
                    }
                    var playerChests = playerEntry.Value;

                    foreach (KeyValuePair<int, Chest> chestEntry in playerChests)
                    {
                        int chestId = chestEntry.Key;
                        // If a chest ID is specified and the current chest does not match, then skip
                        if (ChestId != null && chestId != ChestId)
                        {
                            continue;
                        }
                        var chest = chestEntry.Value;

                        List<JItem> jItems = new List<JItem>(chest.item.Length);

                        foreach (var item in chest.item)
                        {
                            var jItem = new JItem();

                            jItem.id = item.type;
                            jItem.stack = item.stack;
                            jItem.prefix = item.prefix;

                            jItems.Add(jItem);
                        }

                        MemoryStream itemsMs = new MemoryStream();
                        using (var writer = new BsonWriter(itemsMs))
                        {
                            JsonSerializer serializer = new JsonSerializer();
                            serializer.Serialize(writer, jItems);
                        }

                        var sql = @"REPLACE INTO chests (id, playerUuid, x, y, items) VALUES (@id, @playerUuid, @x, @y, @items);";

                        using (var cmd = new SqliteCommand(sql, conn))
                        {
                            cmd.Parameters.AddWithValue("@id", chestId);
                            cmd.Parameters.AddWithValue("@playerUuid", playerUuid);
                            cmd.Parameters.AddWithValue("@x", chest.x);
                            cmd.Parameters.AddWithValue("@y", chest.y);
                            cmd.Parameters.AddWithValue("@items", itemsMs.ToArray());

                            cmd.ExecuteNonQuery();
                        }

                        count++;
                    }
                }

                foreach ((int x, int y) in playerPlacedChests)
                {
                    var sql = @"REPLACE INTO placed (x, y) VALUES (@x, @y);";

                    using (var cmd = new SqliteCommand(sql, conn))
                    {
                        cmd.Parameters.AddWithValue("@x", x);
                        cmd.Parameters.AddWithValue("@y", y);

                        cmd.ExecuteNonQuery();
                    }
                }
            }

            TSPlayer.Server.SendSuccessMessage($"Saved {count} loot chest inventories, {playerPlacedChests.Count} player-placed chests.");
        }

        public Chest GetOrCreateFakeChest(int chestId, string playerUuid)
        {
            var playerChests = fakeChestsMap.GetValueOrDefault(playerUuid, new Dictionary<int, Chest>());
            fakeChestsMap[playerUuid] = playerChests;

            if (!playerChests.ContainsKey(chestId))
            {
                var realChest = Main.chest[chestId];

                // copy the chest data from the real untouched chest
                var fakeChest = new Chest
                {
                    x = realChest.x,
                    y = realChest.y
                };
                realChest.item.CopyTo(fakeChest.item, 0);

                // save it in the fake chest list
                fakeChestsMap[playerUuid][chestId] = fakeChest;

                // save the fake chests list to disk
                SaveFakeChests(playerUuid, chestId);

                return fakeChest;
            }

            return playerChests[chestId];
        }

        public void SetChestPlayerPlaced(int tileX, int tileY)
        {
            playerPlacedChests.Add((tileX, tileY));
        }

        public bool IsChestPlayerPlaced(int tileX, int tileY)
        {
            return playerPlacedChests.Contains((tileX, tileY));
        }
    }

}
