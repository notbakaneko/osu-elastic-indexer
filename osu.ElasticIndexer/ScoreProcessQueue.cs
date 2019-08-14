// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Dapper;
using Dapper.Contrib.Extensions;
using MySql.Data.MySqlClient;

namespace osu.ElasticIndexer
{
    [CursorColumn("queue_id")]
    [Table("score_process_queue")]
    public class ScoreProcessQueue : Model
    {
        public override long CursorValue => QueueId;

        // These are the only columns we care about at the momemnt.
        public uint QueueId { get; set; }

        public ulong ScoreId { get; set; }

        public static void CompleteQueued<T>(List<ScoreProcessQueue> queueItems) where T : HighScore
        {
            if (!queueItems.Any()) return;
            var queueIds = queueItems.Select(x => x.QueueId);

            using (var dbConnection = new MySqlConnection(AppSettings.ConnectionString))
            {
                dbConnection.Open();

                const string query = "update score_process_queue set status = 2 where queue_id in @queueIds";
                dbConnection.Execute(query, new { queueIds });
            }
        }

        public static List<T> FetchByScoreIds<T>(List<ulong> scoreIds) where T : HighScore
        {
            var table = typeof(T).GetCustomAttributes<TableAttribute>().First().Name;

            using (var dbConnection = new MySqlConnection(AppSettings.ConnectionString))
            {
                dbConnection.Open();

                string query = $"select * from {table} where score_id in @scoreIds";
                var parameters = new { scoreIds };
                Console.WriteLine("{0} {1}", query, parameters);

                return dbConnection.Query<T>(query, parameters).AsList();
            }
        }
    }
}
