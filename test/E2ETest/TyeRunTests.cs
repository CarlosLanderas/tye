﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.Tye;
using Microsoft.Tye.ConfigModel;
using Microsoft.Tye.Hosting;
using Microsoft.Tye.Hosting.Model;
using Microsoft.Tye.Hosting.Model.V1;
using Xunit;
using Xunit.Abstractions;

namespace E2ETest
{
    public class TyeRunTests
    {
        private readonly ITestOutputHelper output;
        private readonly TestOutputLogEventSink sink;
        private readonly JsonSerializerOptions _options;

        public TyeRunTests(ITestOutputHelper output)
        {
            this.output = output;
            sink = new TestOutputLogEventSink(output);

            _options = new JsonSerializerOptions()
            {
                PropertyNameCaseInsensitive = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                WriteIndented = true,
            };

            _options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        }

        [Fact]
        public async Task SingleProjectRunTest()
        {
            var projectDirectory = new DirectoryInfo(Path.Combine(TestHelpers.GetSolutionRootDirectory("tye"), "samples", "single-project", "test-project"));
            using var tempDirectory = TempDirectory.Create();
            DirectoryCopy.Copy(projectDirectory.FullName, tempDirectory.DirectoryPath);

            var projectFile = new FileInfo(Path.Combine(tempDirectory.DirectoryPath, "test-project.csproj"));
            using var host = new TyeHost(ConfigFactory.FromFile(projectFile).ToHostingApplication(), Array.Empty<string>())
            {
                Sink = sink,
            };

            await host.StartAsync();
            try
            {
                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (a, b, c, d) => true,
                    AllowAutoRedirect = false
                };

                var client = new HttpClient(new RetryHandler(handler));

                // Make sure dashboard and applications are up.
                // Dashboard should be hosted in same process.
                var dashboardUri = new Uri(host.DashboardWebApplication!.Addresses.First());
                var dashboardString = await client.GetStringAsync($"{dashboardUri}api/v1/services/test-project");

                var service = JsonSerializer.Deserialize<V1Service>(dashboardString, _options);
                var binding = service.Description!.Bindings.Where(b => b.Protocol == "http").Single();
                var uriBackendProcess = new Uri($"{binding.Protocol}://localhost:{binding.Port}");

                // This isn't reliable right now because micronetes only guarantees the process starts, not that
                // that kestrel started.
                try
                {
                    var appResponse = await client.GetAsync(uriBackendProcess);
                    Assert.Equal(HttpStatusCode.OK, appResponse.StatusCode);
                }
                finally
                {
                    // If we failed, there's a good chance the service isn't running. Let's get the logs either way and put
                    // them in the output.
                    var request = new HttpRequestMessage(HttpMethod.Get, new Uri(dashboardUri, $"/api/v1/logs/{service.Description.Name}"));
                    var response = await client.SendAsync(request);
                    var text = await response.Content.ReadAsStringAsync();

                    output.WriteLine($"Logs for service: {service.Description.Name}");
                    output.WriteLine(text);
                }
            }
            finally
            {
                await host.StopAsync();
            }
        }

        [Fact]
        public async Task FrontendBackendRunTest()
        {
            var projectDirectory = new DirectoryInfo(Path.Combine(TestHelpers.GetSolutionRootDirectory("tye"), "samples", "frontend-backend"));
            using var tempDirectory = TempDirectory.Create();
            DirectoryCopy.Copy(projectDirectory.FullName, tempDirectory.DirectoryPath);

            var projectFile = new FileInfo(Path.Combine(tempDirectory.DirectoryPath, "tye.yaml"));
            using var host = new TyeHost(ConfigFactory.FromFile(projectFile).ToHostingApplication(), Array.Empty<string>())
            {
                Sink = sink,
            };

            await host.StartAsync();
            try
            {
                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (a, b, c, d) => true,
                    AllowAutoRedirect = false
                };

                var client = new HttpClient(new RetryHandler(handler));

                var dashboardUri = new Uri(host.DashboardWebApplication!.Addresses.First());

                await CheckServiceIsUp(host.Application, client, "backend", dashboardUri);
                await CheckServiceIsUp(host.Application, client, "frontend", dashboardUri);
            }
            finally
            {
                await host.StopAsync();
            }
        }

        [Fact]
        public async Task IngressRunTest()
        {
            var projectDirectory = new DirectoryInfo(Path.Combine(TestHelpers.GetSolutionRootDirectory("tye"), "samples", "apps-with-ingress"));
            using var tempDirectory = TempDirectory.Create();
            DirectoryCopy.Copy(projectDirectory.FullName, tempDirectory.DirectoryPath);

            var projectFile = new FileInfo(Path.Combine(tempDirectory.DirectoryPath, "tye.yaml"));
            using var host = new TyeHost(ConfigFactory.FromFile(projectFile).ToHostingApplication(), Array.Empty<string>())
            {
                Sink = sink,
            };

            await host.StartAsync();
            try
            {
                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (a, b, c, d) => true,
                    AllowAutoRedirect = false
                };

                var client = new HttpClient(new RetryHandler(handler));

                var serviceApi = new Uri(host.DashboardWebApplication!.Addresses.First());
                var ingressService = await client.GetStringAsync($"{serviceApi}api/v1/services/ingress");

                var service = JsonSerializer.Deserialize<V1Service>(ingressService, _options);
                var binding = service.Description!.Bindings.Single();
                var ingressUri = $"http://localhost:{binding.Port}";

                var rawResponseA = await client.GetStringAsync(ingressUri + "/A");
                var rawResponseB = await client.GetStringAsync(ingressUri + "/B");

                Assert.Equal("Hello from Application A", rawResponseA);
                Assert.Equal("Hello from Application B", rawResponseB);

                var requestA = new HttpRequestMessage(HttpMethod.Get, ingressUri);
                requestA.Headers.Host = "a.example.com";
                var requestB = new HttpRequestMessage(HttpMethod.Get, ingressUri);
                requestB.Headers.Host = "b.example.com";

                var responseA = await client.SendAsync(requestA);
                var responseB = await client.SendAsync(requestB);

                Assert.Equal("Hello from Application A", await responseA.Content.ReadAsStringAsync());
                Assert.Equal("Hello from Application B", await responseB.Content.ReadAsStringAsync());
            }
            finally
            {
                await host.StopAsync();
            }
        }

        [ConditionalFact]
        [SkipIfDockerNotRunning]
        public async Task FrontendBackendRunTestWithDocker()
        {
            var projectDirectory = new DirectoryInfo(Path.Combine(TestHelpers.GetSolutionRootDirectory("tye"), "samples", "frontend-backend"));
            using var tempDirectory = TempDirectory.Create();
            DirectoryCopy.Copy(projectDirectory.FullName, tempDirectory.DirectoryPath);

            var projectFile = new FileInfo(Path.Combine(tempDirectory.DirectoryPath, "tye.yaml"));
            using var host = new TyeHost(ConfigFactory.FromFile(projectFile).ToHostingApplication(), new[] { "--docker" })
            {
                Sink = sink,
            };

            await host.StartAsync();
            try
            {
                // Make sure we're runningn containers
                Assert.True(host.Application.Services.All(s => s.Value.Description.RunInfo is DockerRunInfo));

                var handler = new HttpClientHandler
                {
                    ServerCertificateCustomValidationCallback = (a, b, c, d) => true,
                    AllowAutoRedirect = false
                };

                var client = new HttpClient(new RetryHandler(handler));

                var dashboardUri = new Uri(host.DashboardWebApplication!.Addresses.First());

                await CheckServiceIsUp(host.Application, client, "backend", dashboardUri);
                await CheckServiceIsUp(host.Application, client, "frontend", dashboardUri);
            }
            finally
            {
                await host.StopAsync();
            }
        }

        private async Task CheckServiceIsUp(Microsoft.Tye.Hosting.Model.Application application, HttpClient client, string serviceName, Uri dashboardUri)
        {
            // make sure backend is up before frontend
            var dashboardString = await client.GetStringAsync($"{dashboardUri}api/v1/services/{serviceName}");

            var service = JsonSerializer.Deserialize<V1Service>(dashboardString, _options);
            var binding = service.Description!.Bindings.Where(b => b.Protocol == "http").Single();
            var uriBackendProcess = new Uri($"{binding.Protocol}://localhost:{binding.Port}");

            try
            {
                var appResponse = await client.GetAsync(uriBackendProcess);
                Assert.Equal(HttpStatusCode.OK, appResponse.StatusCode);
                var content = await appResponse.Content.ReadAsStringAsync();
                if (serviceName == "frontend")
                {
                    Assert.Matches("Frontend Listening IP: (.+)\n", content);
                    Assert.Matches("Backend Listening IP: (.+)\n", content);
                }
            }
            finally
            {
                // If we failed, there's a good chance the service isn't running. Let's get the logs either way and put
                // them in the output.
                foreach (var s in application.Services.Values)
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, new Uri(dashboardUri, $"/api/v1/logs/{s.Description.Name}"));
                    var response = await client.SendAsync(request);
                    var text = await response.Content.ReadAsStringAsync();

                    output.WriteLine($"Logs for service: {s.Description.Name}");
                    output.WriteLine(text);
                }
            }
        }
    }
}
