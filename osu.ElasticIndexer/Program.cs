﻿// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using Dapper;
using McMaster.Extensions.CommandLineUtils;
using osu.ElasticIndexer.Commands;
using osu.ElasticIndexer.Commands.Queue;

namespace osu.ElasticIndexer
{
    [Command]
    [Subcommand(typeof(ActiveSchemasCommands))]
    [Subcommand(typeof(ClearQueueCommand))]
    [Subcommand(typeof(CloseIndexCommand))]
    [Subcommand(typeof(DeleteIndexCommand))]
    [Subcommand(typeof(ListIndicesCommand))]
    [Subcommand(typeof(OpenIndexCommand))]
    [Subcommand(typeof(SchemaCommands))]
    [Subcommand(typeof(UpdateAliasCommand))]
    [Subcommand(typeof(QueueCommands))]
    public class Program
    {
        public static void Main(string[] args)
        {
            DefaultTypeMap.MatchNamesWithUnderscores = true;

            CommandLineApplication.Execute<Program>(args);
        }

        public int OnExecute(CommandLineApplication app)
        {
            app.ShowHelp();
            return 1;
        }
    }
}
