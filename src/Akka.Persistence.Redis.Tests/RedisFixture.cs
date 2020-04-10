﻿using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Akka.Util;
using Docker.DotNet;
using Docker.DotNet.Models;
using Xunit;

namespace Akka.Persistence.Redis.Tests
{
    [CollectionDefinition("RedisSpec")]
    public sealed class RedisSpecsFixture : ICollectionFixture<RedisFixture>
    {
    }

    public class RedisFixture : IAsyncLifetime
    {
        protected readonly string RedisContainerName = $"redis-{Guid.NewGuid():N}";
        protected DockerClient Client;

        public RedisFixture()
        {
            DockerClientConfiguration config;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
                config = new DockerClientConfiguration(new Uri("unix://var/run/docker.sock"));
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                config = new DockerClientConfiguration(new Uri("npipe://./pipe/docker_engine"));
            else
                throw new NotSupportedException($"Unsupported OS [{RuntimeInformation.OSDescription}]");

            Client = config.CreateClient();
        }

        protected string RedisImageName => "redis";

        public string ConnectionString { get; private set; }

        public async Task InitializeAsync()
        {
            var images = await Client.Images.ListImagesAsync(new ImagesListParameters { MatchName = RedisImageName });
            if (images.Count == 0)
                await Client.Images.CreateImageAsync(
                    new ImagesCreateParameters { FromImage = RedisImageName, Tag = "latest" }, null,
                    new Progress<JSONMessage>(message =>
                    {
                        Console.WriteLine(!string.IsNullOrEmpty(message.ErrorMessage)
                            ? message.ErrorMessage
                            : $"{message.ID} {message.Status} {message.ProgressMessage}");
                    }));

            var redisHostPort = ThreadLocalRandom.Current.Next(9000, 10000);

            // create the container
            await Client.Containers.CreateContainerAsync(new CreateContainerParameters
            {
                Image = RedisImageName,
                Name = RedisContainerName,
                Tty = true,
                ExposedPorts = new Dictionary<string, EmptyStruct>
                {
                    {"6379/tcp", new EmptyStruct()}
                },
                HostConfig = new HostConfig
                {
                    PortBindings = new Dictionary<string, IList<PortBinding>>
                    {
                        {
                            "6379/tcp",
                            new List<PortBinding>
                            {
                                new PortBinding
                                {
                                    HostPort = $"{redisHostPort}"
                                }
                            }
                        }
                    }
                }
            });

            
            // start the container
            await Client.Containers.StartContainerAsync(RedisContainerName, new ContainerStartParameters());

            // Provide a 30 second startup delay
            await Task.Delay(TimeSpan.FromSeconds(10));

            ConnectionString = $"localhost:{redisHostPort}";
        }

        public async Task DisposeAsync()
        {
            if (Client != null)
            {
                await Client.Containers.KillContainerAsync(RedisContainerName, new ContainerKillParameters());
                // await Client.Containers.StopContainerAsync(RedisContainerName, new ContainerStopParameters { WaitBeforeKillSeconds = 5 });
                await Client.Containers.RemoveContainerAsync(RedisContainerName,
                    new ContainerRemoveParameters { Force = true });
                Client.Dispose();
            }
        }
    }
}