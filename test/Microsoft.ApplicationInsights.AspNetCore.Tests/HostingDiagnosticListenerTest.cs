﻿namespace Microsoft.ApplicationInsights.AspNetCore.Tests
{
    using System;
    using System.Collections.Concurrent;
    using System.Diagnostics;
    using System.Globalization;
    using System.Linq;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using Microsoft.ApplicationInsights.AspNetCore.DiagnosticListeners;
    using Microsoft.ApplicationInsights.AspNetCore.Tests.Helpers;
    using Microsoft.ApplicationInsights.Channel;
    using Microsoft.ApplicationInsights.Common;
    using Microsoft.ApplicationInsights.DataContracts;
    using Microsoft.ApplicationInsights.Extensibility;
    using Microsoft.ApplicationInsights.Extensibility.Implementation;
    using Microsoft.ApplicationInsights.Extensibility.W3C;
    using Microsoft.AspNetCore.Http;
    using Xunit;

    public class HostingDiagnosticListenerTest : IDisposable
    {
        private const string HttpRequestScheme = "http";
        private const string ExpectedAppId = "cid-v1:some-app-id";
        private const string ActivityCreatedByHostingDiagnosticListener = "ActivityCreatedByHostingDiagnosticListener";

        private static readonly HostString HttpRequestHost = new HostString("testHost");
        private static readonly PathString HttpRequestPath = new PathString("/path/path");
        private static readonly QueryString HttpRequestQueryString = new QueryString("?query=1");

        private static Uri CreateUri(string scheme, HostString host, PathString? path = null, QueryString? query = null)
        {
            string uriString = string.Format(CultureInfo.InvariantCulture, "{0}://{1}", scheme, host);
            if (path != null)
            {
                uriString += path.Value;
            }
            if (query != null)
            {
                uriString += query.Value;
            }
            return new Uri(uriString);
        }

        private HttpContext CreateContext(string scheme, HostString host, PathString? path = null, QueryString? query = null, string method = null)
        {
            HttpContext context = new DefaultHttpContext();
            context.Request.Scheme = scheme;
            context.Request.Host = host;

            if (path.HasValue)
            {
                context.Request.Path = path.Value;
            }

            if (query.HasValue)
            {
                context.Request.QueryString = query.Value;
            }

            if (!string.IsNullOrEmpty(method))
            {
                context.Request.Method = method;
            }

            Assert.Null(context.Features.Get<RequestTelemetry>());

            return context;
        }

        private ConcurrentQueue<ITelemetry> sentTelemetry = new ConcurrentQueue<ITelemetry>();
        private ActiveSubsciptionManager subscriptionManager; 

        private HostingDiagnosticListener CreateHostingListener(AspNetCoreMajorVersion aspNetCoreMajorVersion, TelemetryConfiguration config = null)
        {
            HostingDiagnosticListener hostingListener;
            if (config != null)
            {
                hostingListener = new HostingDiagnosticListener(
                    config,
                    CommonMocks.MockTelemetryClient(telemetry => this.sentTelemetry.Enqueue(telemetry)),
                    CommonMocks.GetMockApplicationIdProvider(),
                    injectResponseHeaders: true,
                    trackExceptions: true,
                    enableW3CHeaders: false,
                    enableNewDiagnosticEvents: (aspNetCoreMajorVersion == AspNetCoreMajorVersion.Two));
            }
            else
            {
                hostingListener = new HostingDiagnosticListener(
                    CommonMocks.MockTelemetryClient(telemetry => this.sentTelemetry.Enqueue(telemetry)),
                    CommonMocks.GetMockApplicationIdProvider(),
                    injectResponseHeaders: true,
                    trackExceptions: true,
                    enableW3CHeaders: false,
                    enableNewDiagnosticEvents: (aspNetCoreMajorVersion == AspNetCoreMajorVersion.Two));
            }

            hostingListener.OnSubscribe();
            return hostingListener;
        }

        [Theory]
        [InlineData(AspNetCoreMajorVersion.One)]
        [InlineData(AspNetCoreMajorVersion.Two)]
        public void TestConditionalAppIdFlagIsRespected(AspNetCoreMajorVersion aspNetCoreMajorVersion)
        {
            HttpContext context = CreateContext(HttpRequestScheme, HttpRequestHost);
            TelemetryConfiguration config = TelemetryConfiguration.CreateDefault();
            // This flag tells sdk to not add app id in response header, unless its received in incoming headers.
            // For tests, no incoming headers is add, so the response should not have app id as well.
            config.ExperimentalFeatures.Add("conditionalAppId");

            using (var hostingListener = CreateHostingListener(aspNetCoreMajorVersion, config))
            {
                HandleRequestBegin(hostingListener, context, 0, aspNetCoreMajorVersion);

                Assert.NotNull(context.Features.Get<RequestTelemetry>());

                // VALIDATE
                Assert.Null(HttpHeadersUtilities.GetRequestContextKeyValue(context.Response.Headers,
                        RequestResponseHeaders.RequestContextTargetKey));

                HandleRequestEnd(hostingListener, context, 0, aspNetCoreMajorVersion);
            }

            Assert.Single(sentTelemetry);
            Assert.IsType<RequestTelemetry>(this.sentTelemetry.First());

            RequestTelemetry requestTelemetry = this.sentTelemetry.First() as RequestTelemetry;
            Assert.True(requestTelemetry.Duration.TotalMilliseconds >= 0);
            Assert.True(requestTelemetry.Success);
            Assert.Equal(CommonMocks.InstrumentationKey, requestTelemetry.Context.InstrumentationKey);
            Assert.True(string.IsNullOrEmpty(requestTelemetry.Source));
            Assert.Equal(CreateUri(HttpRequestScheme, HttpRequestHost), requestTelemetry.Url);
            Assert.NotEmpty(requestTelemetry.Context.GetInternalContext().SdkVersion);
            Assert.Contains(SdkVersionTestUtils.VersionPrefix, requestTelemetry.Context.GetInternalContext().SdkVersion);
        }

        [Theory]
        [InlineData(AspNetCoreMajorVersion.One)]
        [InlineData(AspNetCoreMajorVersion.Two)]
        public void TestSdkVersionIsPopulatedByMiddleware(AspNetCoreMajorVersion aspNetCoreMajorVersion)
        {
            HttpContext context = CreateContext(HttpRequestScheme, HttpRequestHost);
            TelemetryConfiguration config = TelemetryConfiguration.CreateDefault();

            using (var hostingListener = CreateHostingListener(aspNetCoreMajorVersion, config))
            {
                HandleRequestBegin(hostingListener, context, 0, aspNetCoreMajorVersion);

                Assert.NotNull(context.Features.Get<RequestTelemetry>());
                HandleRequestEnd(hostingListener, context, 0, aspNetCoreMajorVersion);
            }

            Assert.Single(sentTelemetry);
            Assert.IsType<RequestTelemetry>(this.sentTelemetry.First());

            RequestTelemetry requestTelemetry = this.sentTelemetry.First() as RequestTelemetry;
            Assert.True(requestTelemetry.Duration.TotalMilliseconds >= 0);
            Assert.True(requestTelemetry.Success);
            Assert.Equal(CommonMocks.InstrumentationKey, requestTelemetry.Context.InstrumentationKey);
            Assert.True(string.IsNullOrEmpty(requestTelemetry.Source));
            Assert.Equal(CreateUri(HttpRequestScheme, HttpRequestHost), requestTelemetry.Url);
            Assert.NotEmpty(requestTelemetry.Context.GetInternalContext().SdkVersion);
            Assert.Contains(SdkVersionTestUtils.VersionPrefix, requestTelemetry.Context.GetInternalContext().SdkVersion);
        }

        [Theory]
        [InlineData(AspNetCoreMajorVersion.One)]
        [InlineData(AspNetCoreMajorVersion.Two)]
        public void TestRequestUriIsPopulatedByMiddleware(AspNetCoreMajorVersion aspNetCoreMajorVersion)
        {
            HttpContext context = CreateContext(HttpRequestScheme, HttpRequestHost, HttpRequestPath, HttpRequestQueryString);

            using (var hostingListener = CreateHostingListener(aspNetCoreMajorVersion))
            {
                HandleRequestBegin(hostingListener, context, 0, aspNetCoreMajorVersion);

                Assert.NotNull(context.Features.Get<RequestTelemetry>());
                Assert.Equal(CommonMocks.TestApplicationId,
                    HttpHeadersUtilities.GetRequestContextKeyValue(context.Response.Headers, RequestResponseHeaders.RequestContextTargetKey));

                HandleRequestEnd(hostingListener, context, 0, aspNetCoreMajorVersion);
            }

            Assert.Single(sentTelemetry);
            Assert.IsType<RequestTelemetry>(this.sentTelemetry.First());
            RequestTelemetry requestTelemetry = sentTelemetry.First() as RequestTelemetry;
            Assert.NotNull(requestTelemetry.Url);
            Assert.True(requestTelemetry.Duration.TotalMilliseconds >= 0);
            Assert.True(requestTelemetry.Success);
            Assert.Equal(CommonMocks.InstrumentationKey, requestTelemetry.Context.InstrumentationKey);
            Assert.True(string.IsNullOrEmpty(requestTelemetry.Source));
            Assert.Equal(CreateUri(HttpRequestScheme, HttpRequestHost, HttpRequestPath, HttpRequestQueryString), requestTelemetry.Url);
            Assert.NotEmpty(requestTelemetry.Context.GetInternalContext().SdkVersion);
            Assert.Contains(SdkVersionTestUtils.VersionPrefix, requestTelemetry.Context.GetInternalContext().SdkVersion);
        }

        [Theory]
        [InlineData(AspNetCoreMajorVersion.One)]
        [InlineData(AspNetCoreMajorVersion.Two)]
        public void RequestWillBeMarkedAsFailedForRunawayException(AspNetCoreMajorVersion aspNetCoreMajorVersion)
        {
            HttpContext context = CreateContext(HttpRequestScheme, HttpRequestHost);

            using (var hostingListener = CreateHostingListener(aspNetCoreMajorVersion))
            {
                HandleRequestBegin(hostingListener, context, 0, aspNetCoreMajorVersion);

                Assert.NotNull(context.Features.Get<RequestTelemetry>());
                Assert.Equal(CommonMocks.TestApplicationId,
                    HttpHeadersUtilities.GetRequestContextKeyValue(context.Response.Headers, RequestResponseHeaders.RequestContextTargetKey));

                hostingListener.OnDiagnosticsUnhandledException(context, null);
                HandleRequestEnd(hostingListener, context, 0, aspNetCoreMajorVersion);
            }

            var telemetries = sentTelemetry.ToArray();
            Assert.Equal(2, sentTelemetry.Count);
            Assert.IsType<ExceptionTelemetry>(telemetries[0]);
            
            Assert.IsType<RequestTelemetry>(telemetries[1]);
            RequestTelemetry requestTelemetry = telemetries[1] as RequestTelemetry;
            Assert.True(requestTelemetry.Duration.TotalMilliseconds >= 0);
            Assert.False(requestTelemetry.Success);
            Assert.Equal(CommonMocks.InstrumentationKey, requestTelemetry.Context.InstrumentationKey);
            Assert.True(string.IsNullOrEmpty(requestTelemetry.Source));
            Assert.Equal(CreateUri(HttpRequestScheme, HttpRequestHost), requestTelemetry.Url);
            Assert.NotEmpty(requestTelemetry.Context.GetInternalContext().SdkVersion);
            Assert.Contains(SdkVersionTestUtils.VersionPrefix, requestTelemetry.Context.GetInternalContext().SdkVersion);
        }

        [Theory]
        [InlineData(AspNetCoreMajorVersion.One)]
        [InlineData(AspNetCoreMajorVersion.Two)]
        public void OnBeginRequestCreateNewActivityAndInitializeRequestTelemetry(AspNetCoreMajorVersion aspNetCoreMajorVersion)
        {
            HttpContext context = CreateContext(HttpRequestScheme, HttpRequestHost, "/Test", method: "POST");

            using (var hostingListener = CreateHostingListener(aspNetCoreMajorVersion))
            {
                HandleRequestBegin(hostingListener, context, 0, aspNetCoreMajorVersion);

                Assert.NotNull(Activity.Current);
                Assert.Equal(ActivityCreatedByHostingDiagnosticListener, Activity.Current.OperationName);

                var requestTelemetry = context.Features.Get<RequestTelemetry>();
                Assert.NotNull(requestTelemetry);

                if(true)
                {
                    Assert.Equal(requestTelemetry.Id, FormatTelemetryId(Activity.Current.TraceId.ToHexString(), Activity.Current.SpanId.ToHexString()));
                    Assert.Equal(requestTelemetry.Context.Operation.Id, Activity.Current.TraceId.ToHexString());
                    Assert.Null(requestTelemetry.Context.Operation.ParentId);
                }
                else
                {
                    Assert.Equal(requestTelemetry.Id, Activity.Current.Id);
                    Assert.Equal(requestTelemetry.Context.Operation.Id, Activity.Current.RootId);
                    Assert.Null(requestTelemetry.Context.Operation.ParentId);
                }

                // W3C compatible-Id ( should go away when W3C is implemented in .NET https://github.com/dotnet/corefx/issues/30331)
                Assert.Equal(32, requestTelemetry.Context.Operation.Id.Length);
                Assert.True(Regex.Match(requestTelemetry.Context.Operation.Id, @"[a-z][0-9]").Success);
                // end of workaround test
            }
        }

        [Fact]
        public void OnBeginRequestCreateNewActivityAndInitializeRequestTelemetryFromRequestIdHeader()
        {
            // This tests 1.XX scenario where SDK is responsible for reading Correlation-Context and populate Activity.Baggage
            HttpContext context = CreateContext(HttpRequestScheme, HttpRequestHost, "/Test", method: "POST");
            var requestId = Guid.NewGuid().ToString();
            context.Request.Headers[RequestResponseHeaders.RequestIdHeader] = requestId;
            context.Request.Headers[RequestResponseHeaders.CorrelationContextHeader] = "prop1=value1, prop2=value2";

            using (var hostingListener = CreateHostingListener(AspNetCoreMajorVersion.One))
            {
                HandleRequestBegin(hostingListener, context, 0, AspNetCoreMajorVersion.One);

                Assert.NotNull(Activity.Current);
                Assert.Single(Activity.Current.Baggage.Where(b => b.Key == "prop1" && b.Value == "value1"));
                Assert.Single(Activity.Current.Baggage.Where(b => b.Key == "prop2" && b.Value == "value2"));

                var requestTelemetry = context.Features.Get<RequestTelemetry>();
                Assert.NotNull(requestTelemetry);
                Assert.Equal(requestTelemetry.Id, Activity.Current.Id);
                Assert.Equal(requestTelemetry.Context.Operation.Id, Activity.Current.RootId);
                Assert.Equal(requestTelemetry.Context.Operation.ParentId, requestId);
                Assert.Equal("value1", requestTelemetry.Properties["prop1"]);
                Assert.Equal("value2", requestTelemetry.Properties["prop2"]);
            }
        }

        [Fact]
        public void OnHttpRequestInStartInitializeTelemetryIfActivityParentIdIsNotNull()
        {
            var context = CreateContext(HttpRequestScheme, HttpRequestHost, "/Test", method: "POST");
            var activity = new Activity("operation");
            activity.SetParentId(Guid.NewGuid().ToString());
            activity.AddBaggage("item1", "value1");
            activity.AddBaggage("item2", "value2");

            activity.Start();

            using (var hostingListener = CreateHostingListener(AspNetCoreMajorVersion.Two))
            {
                HandleRequestBegin(hostingListener, context, 0, AspNetCoreMajorVersion.Two);
                HandleRequestEnd(hostingListener, context, 0, AspNetCoreMajorVersion.Two);
            }

            Assert.Single(sentTelemetry);
            var requestTelemetry = this.sentTelemetry.First() as RequestTelemetry;

            Assert.Equal(requestTelemetry.Id, activity.Id);
            Assert.Equal(requestTelemetry.Context.Operation.Id, activity.RootId);
            Assert.Equal(requestTelemetry.Context.Operation.ParentId, activity.ParentId);
            Assert.Equal(requestTelemetry.Properties.Count, activity.Baggage.Count());

            foreach (var prop in activity.Baggage)
            {
                Assert.True(requestTelemetry.Properties.ContainsKey(prop.Key));
                Assert.Equal(requestTelemetry.Properties[prop.Key], prop.Value);
            }
        }

        [Theory]
        [InlineData(AspNetCoreMajorVersion.One)]
        [InlineData(AspNetCoreMajorVersion.Two)]
        public void OnEndRequestSetsRequestNameToMethodAndPathForPostRequest(AspNetCoreMajorVersion aspNetCoreMajorVersion)
        {
            HttpContext context = CreateContext(HttpRequestScheme, HttpRequestHost, "/Test", method: "POST");

            using (var hostingListener = CreateHostingListener(aspNetCoreMajorVersion))
            {
                HandleRequestBegin(hostingListener, context, 0, aspNetCoreMajorVersion);

                Assert.NotNull(context.Features.Get<RequestTelemetry>());
                Assert.Equal(CommonMocks.TestApplicationId,
                    HttpHeadersUtilities.GetRequestContextKeyValue(context.Response.Headers, RequestResponseHeaders.RequestContextTargetKey));

                HandleRequestEnd(hostingListener, context, 0, aspNetCoreMajorVersion);
            }

            Assert.Single(sentTelemetry);
            Assert.IsType<RequestTelemetry>(this.sentTelemetry.First());
            RequestTelemetry requestTelemetry = this.sentTelemetry.Single() as RequestTelemetry;
            Assert.True(requestTelemetry.Duration.TotalMilliseconds >= 0);
            Assert.True(requestTelemetry.Success);
            Assert.Equal(CommonMocks.InstrumentationKey, requestTelemetry.Context.InstrumentationKey);
            Assert.True(string.IsNullOrEmpty(requestTelemetry.Source));
            Assert.Equal(CreateUri(HttpRequestScheme, HttpRequestHost, "/Test"), requestTelemetry.Url);
            Assert.NotEmpty(requestTelemetry.Context.GetInternalContext().SdkVersion);
            Assert.Contains(SdkVersionTestUtils.VersionPrefix, requestTelemetry.Context.GetInternalContext().SdkVersion);
            Assert.Equal("POST /Test", requestTelemetry.Name);
        }

        [Theory]
        [InlineData(AspNetCoreMajorVersion.One)]
        [InlineData(AspNetCoreMajorVersion.Two)]
        public void OnEndRequestSetsRequestNameToMethodAndPath(AspNetCoreMajorVersion aspNetCoreMajorVersion)
        {
            HttpContext context = CreateContext(HttpRequestScheme, HttpRequestHost, "/Test", method: "GET");
            TelemetryConfiguration config = TelemetryConfiguration.CreateDefault();

            using (var hostingListener = CreateHostingListener(aspNetCoreMajorVersion, config))
            {
                HandleRequestBegin(hostingListener, context, 0, aspNetCoreMajorVersion);

                Assert.NotNull(context.Features.Get<RequestTelemetry>());

                HandleRequestEnd(hostingListener, context, 0, aspNetCoreMajorVersion);
            }

            Assert.NotNull(this.sentTelemetry);
            Assert.IsType<RequestTelemetry>(this.sentTelemetry.First());
            RequestTelemetry requestTelemetry = this.sentTelemetry.First() as RequestTelemetry;
            Assert.True(requestTelemetry.Duration.TotalMilliseconds >= 0);
            Assert.True(requestTelemetry.Success);
            Assert.Equal(CommonMocks.InstrumentationKey, requestTelemetry.Context.InstrumentationKey);
            Assert.True(string.IsNullOrEmpty(requestTelemetry.Source));            
            Assert.Equal(CreateUri(HttpRequestScheme, HttpRequestHost, "/Test"), requestTelemetry.Url);
            Assert.NotEmpty(requestTelemetry.Context.GetInternalContext().SdkVersion);
            Assert.Contains(SdkVersionTestUtils.VersionPrefix, requestTelemetry.Context.GetInternalContext().SdkVersion);
            Assert.Equal("GET /Test", requestTelemetry.Name);
        }

        [Theory]
        [InlineData(AspNetCoreMajorVersion.One)]
        [InlineData(AspNetCoreMajorVersion.Two)]
        public void OnEndRequestFromSameInstrumentationKey(AspNetCoreMajorVersion aspNetCoreMajorVersion)
        {
            HttpContext context = CreateContext(HttpRequestScheme, HttpRequestHost, "/Test", method: "GET");
            HttpHeadersUtilities.SetRequestContextKeyValue(context.Request.Headers, RequestResponseHeaders.RequestContextSourceKey, CommonMocks.TestApplicationId);            

            using (var hostingListener = CreateHostingListener(aspNetCoreMajorVersion))
            {
                HandleRequestBegin(hostingListener, context, 0, aspNetCoreMajorVersion);

                Assert.NotNull(context.Features.Get<RequestTelemetry>());
                Assert.Equal(CommonMocks.TestApplicationId,
                    HttpHeadersUtilities.GetRequestContextKeyValue(context.Response.Headers, RequestResponseHeaders.RequestContextTargetKey));                

                HandleRequestEnd(hostingListener, context, 0, aspNetCoreMajorVersion);
            }

            Assert.NotNull(this.sentTelemetry);
            Assert.IsType<RequestTelemetry>(this.sentTelemetry.First());
            RequestTelemetry requestTelemetry = this.sentTelemetry.First() as RequestTelemetry;
            Assert.True(requestTelemetry.Duration.TotalMilliseconds >= 0);
            Assert.True(requestTelemetry.Success);
            Assert.Equal(CommonMocks.InstrumentationKey, requestTelemetry.Context.InstrumentationKey);
            Assert.True(string.IsNullOrEmpty(requestTelemetry.Source));
            Assert.Equal(CreateUri(HttpRequestScheme, HttpRequestHost, "/Test"), requestTelemetry.Url);
            Assert.NotEmpty(requestTelemetry.Context.GetInternalContext().SdkVersion);
            Assert.Contains(SdkVersionTestUtils.VersionPrefix, requestTelemetry.Context.GetInternalContext().SdkVersion);
            Assert.Equal("GET /Test", requestTelemetry.Name);
        }

        [Theory]
        [InlineData(AspNetCoreMajorVersion.One)]
        [InlineData(AspNetCoreMajorVersion.Two)]
        public void OnEndRequestFromDifferentInstrumentationKey(AspNetCoreMajorVersion aspNetCoreMajorVersion)
        {
            HttpContext context = CreateContext(HttpRequestScheme, HttpRequestHost, "/Test", method: "GET");
            HttpHeadersUtilities.SetRequestContextKeyValue(context.Request.Headers, RequestResponseHeaders.RequestContextSourceKey, "DIFFERENT_INSTRUMENTATION_KEY_HASH");

            using (var hostingListener = CreateHostingListener(aspNetCoreMajorVersion))
            {
                HandleRequestBegin(hostingListener, context, 0, aspNetCoreMajorVersion);

                Assert.NotNull(context.Features.Get<RequestTelemetry>());
                Assert.Equal(CommonMocks.TestApplicationId,
                    HttpHeadersUtilities.GetRequestContextKeyValue(context.Response.Headers,
                        RequestResponseHeaders.RequestContextTargetKey));

                HandleRequestEnd(hostingListener, context, 0, aspNetCoreMajorVersion);
            }

            Assert.Single(sentTelemetry);
            Assert.IsType<RequestTelemetry>(this.sentTelemetry.First());
            RequestTelemetry requestTelemetry = this.sentTelemetry.First() as RequestTelemetry;
            Assert.True(requestTelemetry.Duration.TotalMilliseconds >= 0);
            Assert.True(requestTelemetry.Success);
            Assert.Equal(CommonMocks.InstrumentationKey, requestTelemetry.Context.InstrumentationKey);
            Assert.Equal("DIFFERENT_INSTRUMENTATION_KEY_HASH", requestTelemetry.Source);            
            Assert.Equal(CreateUri(HttpRequestScheme, HttpRequestHost, "/Test"), requestTelemetry.Url);
            Assert.NotEmpty(requestTelemetry.Context.GetInternalContext().SdkVersion);
            Assert.Contains(SdkVersionTestUtils.VersionPrefix, requestTelemetry.Context.GetInternalContext().SdkVersion);
            Assert.Equal("GET /Test", requestTelemetry.Name);
        }

        [Theory]
        [InlineData(AspNetCoreMajorVersion.One)]
        [InlineData(AspNetCoreMajorVersion.Two)]
        public async void SimultaneousRequestsGetDifferentIds(AspNetCoreMajorVersion aspNetCoreMajorVersion)
        {
            var context1 = new DefaultHttpContext();
            context1.Request.Scheme = HttpRequestScheme;
            context1.Request.Host = HttpRequestHost;
            context1.Request.Method = "GET";
            context1.Request.Path = "/Test?id=1";

            var context2 = new DefaultHttpContext();
            context2.Request.Scheme = HttpRequestScheme;
            context2.Request.Host = HttpRequestHost;
            context2.Request.Method = "GET";
            context2.Request.Path = "/Test?id=2";

            using (var hostingListener = CreateHostingListener(aspNetCoreMajorVersion))
            {
                var task1 = Task.Run(() =>
                {
                    var act = new Activity("operation1");
                    act.Start();
                    HandleRequestBegin(hostingListener, context1, 0, aspNetCoreMajorVersion);
                    HandleRequestEnd(hostingListener, context1, 0, aspNetCoreMajorVersion);
                });

                var task2 = Task.Run(() =>
                {
                    var act = new Activity("operation2");
                    act.Start();
                    HandleRequestBegin(hostingListener, context2, 0, aspNetCoreMajorVersion);
                    HandleRequestEnd(hostingListener, context2, 0, aspNetCoreMajorVersion);
                });

                await Task.WhenAll(task1, task2);

                Assert.Equal(2, sentTelemetry.Count);

                var telemetries = this.sentTelemetry.ToArray();
                Assert.IsType<RequestTelemetry>(telemetries[0]);
                Assert.IsType<RequestTelemetry>(telemetries[1]);
                var id1 = ((RequestTelemetry) telemetries[0]).Id;
                var id2 = ((RequestTelemetry) telemetries[1]).Id;
                Assert.NotEqual(id1, id2);
            }
        }

        [Fact]
        public void SimultaneousRequestsGetCorrectDurations()
        {
            var context1 = new DefaultHttpContext();
            context1.Request.Scheme = HttpRequestScheme;
            context1.Request.Host = HttpRequestHost;
            context1.Request.Method = "GET";
            context1.Request.Path = "/Test?id=1";

            var context2 = new DefaultHttpContext();
            context2.Request.Scheme = HttpRequestScheme;
            context2.Request.Host = HttpRequestHost;
            context2.Request.Method = "GET";
            context2.Request.Path = "/Test?id=2";

            long startTime = Stopwatch.GetTimestamp();
            long simulatedSeconds = Stopwatch.Frequency;

            using (var hostingListener = CreateHostingListener(AspNetCoreMajorVersion.One))
            {
                HandleRequestBegin(hostingListener, context1, startTime, AspNetCoreMajorVersion.One);
                HandleRequestBegin(hostingListener, context2, startTime + simulatedSeconds, AspNetCoreMajorVersion.One);
                HandleRequestEnd(hostingListener, context1, startTime + simulatedSeconds * 5, AspNetCoreMajorVersion.One);
                HandleRequestEnd(hostingListener, context2, startTime + simulatedSeconds * 10, AspNetCoreMajorVersion.One);
            }

            var telemetries = this.sentTelemetry.ToArray();
            Assert.Equal(2, telemetries.Length);
            Assert.Equal(TimeSpan.FromSeconds(5), ((RequestTelemetry)telemetries[0]).Duration);
            Assert.Equal(TimeSpan.FromSeconds(9), ((RequestTelemetry)telemetries[1]).Duration);
        }

        [Fact]
        public void OnEndRequestSetsPreciseDurations()
        {
            var context = new DefaultHttpContext();
            context.Request.Scheme = HttpRequestScheme;
            context.Request.Host = HttpRequestHost;
            context.Request.Method = "GET";
            context.Request.Path = "/Test?id=1";

            long startTime = Stopwatch.GetTimestamp();
            using (var hostingListener = CreateHostingListener(AspNetCoreMajorVersion.One))
            {
                HandleRequestBegin(hostingListener, context, startTime, AspNetCoreMajorVersion.One);

                var expectedDuration = TimeSpan.Parse("00:00:01.2345670");
                double durationInStopwatchTicks = Stopwatch.Frequency * expectedDuration.TotalSeconds;

                HandleRequestEnd(hostingListener, context, startTime + (long) durationInStopwatchTicks, AspNetCoreMajorVersion.One);

                Assert.Single(sentTelemetry);
                Assert.Equal(Math.Round(expectedDuration.TotalMilliseconds, 3),
                    Math.Round(((RequestTelemetry) sentTelemetry.First()).Duration.TotalMilliseconds, 3));
            }
        }

        [Theory]
        [InlineData(AspNetCoreMajorVersion.One)]
        [InlineData(AspNetCoreMajorVersion.Two)]
        public void SetsSourceProvidedInHeaders(AspNetCoreMajorVersion aspNetCoreMajorVersion)
        {
            HttpContext context = CreateContext(HttpRequestScheme, HttpRequestHost);
            HttpHeadersUtilities.SetRequestContextKeyValue(context.Request.Headers, RequestResponseHeaders.RequestContextTargetKey, "someAppId");

            using (var hostingListener = CreateHostingListener(aspNetCoreMajorVersion))
            {
                HandleRequestBegin(hostingListener, context, 0, aspNetCoreMajorVersion);
                HandleRequestEnd(hostingListener, context, 0, aspNetCoreMajorVersion);
            }

            Assert.Single(sentTelemetry);
            Assert.IsType<RequestTelemetry>(this.sentTelemetry.Single());
            RequestTelemetry requestTelemetry = this.sentTelemetry.OfType<RequestTelemetry>().Single();

            Assert.Equal("someAppId", requestTelemetry.Source);
        }

        [Theory]
        [InlineData(AspNetCoreMajorVersion.One)]
        [InlineData(AspNetCoreMajorVersion.Two)]
        public void ResponseHeadersAreNotInjectedWhenDisabled(AspNetCoreMajorVersion aspNetCoreMajorVersion)
        {
            HttpContext context = CreateContext(HttpRequestScheme, HttpRequestHost);

            using (var noHeadersMiddleware = new HostingDiagnosticListener(
                CommonMocks.MockTelemetryClient(telemetry => this.sentTelemetry.Enqueue(telemetry)),
                CommonMocks.GetMockApplicationIdProvider(),
                injectResponseHeaders: false,
                trackExceptions: true,
                enableW3CHeaders: false,
                enableNewDiagnosticEvents: (aspNetCoreMajorVersion == AspNetCoreMajorVersion.Two)))
            {
                noHeadersMiddleware.OnSubscribe();

                HandleRequestBegin(noHeadersMiddleware, context, 0, aspNetCoreMajorVersion);
                Assert.False(context.Response.Headers.ContainsKey(RequestResponseHeaders.RequestContextHeader));

                HandleRequestEnd(noHeadersMiddleware, context, 0, aspNetCoreMajorVersion);
                Assert.False(context.Response.Headers.ContainsKey(RequestResponseHeaders.RequestContextHeader));

                Assert.Single(sentTelemetry);
                Assert.IsType<RequestTelemetry>(this.sentTelemetry.First());
            }
        }

        [Theory]
        [InlineData(AspNetCoreMajorVersion.One)]
        [InlineData(AspNetCoreMajorVersion.Two)]
        public void ExceptionsAreNotTrackedInjectedWhenDisabled(AspNetCoreMajorVersion aspNetCoreMajorVersion)
        {
            HttpContext context = CreateContext(HttpRequestScheme, HttpRequestHost);
            using (var noExceptionsMiddleware = new HostingDiagnosticListener(
                CommonMocks.MockTelemetryClient(telemetry => this.sentTelemetry.Enqueue(telemetry)),
                CommonMocks.GetMockApplicationIdProvider(),
                injectResponseHeaders: true,
                trackExceptions: false,
                enableW3CHeaders: false,
                enableNewDiagnosticEvents: (aspNetCoreMajorVersion == AspNetCoreMajorVersion.Two)))
            {
                noExceptionsMiddleware.OnSubscribe();
                noExceptionsMiddleware.OnHostingException(context, new Exception("HostingException"));
                noExceptionsMiddleware.OnDiagnosticsHandledException(context,
                    new Exception("DiagnosticsHandledException"));
                noExceptionsMiddleware.OnDiagnosticsUnhandledException(context, new Exception("UnhandledException"));
            }

            Assert.Empty(sentTelemetry);
        }

        [Theory]
        [InlineData(AspNetCoreMajorVersion.One)]
        [InlineData(AspNetCoreMajorVersion.Two)]
        public void DoesntAddSourceIfRequestHeadersDontHaveSource(AspNetCoreMajorVersion aspNetCoreMajorVersion)
        {
            HttpContext context = CreateContext(HttpRequestScheme, HttpRequestHost);

            using (var hostingListener = CreateHostingListener(aspNetCoreMajorVersion))
            {
                HandleRequestBegin(hostingListener, context, 0, aspNetCoreMajorVersion);
                HandleRequestEnd(hostingListener, context, 0, aspNetCoreMajorVersion);
            }

            Assert.Single(sentTelemetry);
            Assert.IsType<RequestTelemetry>(this.sentTelemetry.Single());
            RequestTelemetry requestTelemetry = this.sentTelemetry.OfType<RequestTelemetry>().Single();

            Assert.True(string.IsNullOrEmpty(requestTelemetry.Source));
        }

        [Theory]
        [InlineData(AspNetCoreMajorVersion.One)]
        [InlineData(AspNetCoreMajorVersion.Two)]
        public void OnBeginRequestWithW3CHeadersIsTrackedCorrectly(AspNetCoreMajorVersion aspNetCoreMajorVersion)
        {
            var configuration = TelemetryConfiguration.CreateDefault();
            configuration.TelemetryInitializers.Add(new W3COperationCorrelationTelemetryInitializer());
            using (var hostingListener = new HostingDiagnosticListener(
                CommonMocks.MockTelemetryClient(telemetry => this.sentTelemetry.Enqueue(telemetry), configuration),
                CommonMocks.GetMockApplicationIdProvider(),
                injectResponseHeaders: true,
                trackExceptions: true,
                enableW3CHeaders: true,
                enableNewDiagnosticEvents: (aspNetCoreMajorVersion == AspNetCoreMajorVersion.Two)))
            {
                hostingListener.OnSubscribe();
                var context = CreateContext(HttpRequestScheme, HttpRequestHost, "/Test", method: "POST");

                context.Request.Headers[W3C.W3CConstants.TraceParentHeader] =
                    "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
                context.Request.Headers[W3C.W3CConstants.TraceStateHeader] = "state=some";
                context.Request.Headers[RequestResponseHeaders.CorrelationContextHeader] = "k=v";
                context.Request.Headers[RequestResponseHeaders.RequestContextHeader] = "appId=something";

                HandleRequestBegin(hostingListener, context, 0, aspNetCoreMajorVersion);

                var activityInitializedByW3CHeader = Activity.Current;
                Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", activityInitializedByW3CHeader.GetTraceId());
                Assert.Equal("00f067aa0ba902b7", activityInitializedByW3CHeader.GetParentSpanId());
                Assert.Equal(16, activityInitializedByW3CHeader.GetSpanId().Length);
                Assert.Equal("state=some", activityInitializedByW3CHeader.GetTracestate());
                Assert.Equal("v", activityInitializedByW3CHeader.Baggage.Single(t => t.Key == "k").Value);

                HandleRequestEnd(hostingListener, context, 0, aspNetCoreMajorVersion);

                Assert.Single(sentTelemetry);
                var requestTelemetry = (RequestTelemetry) this.sentTelemetry.Single();

                Assert.Equal($"|4bf92f3577b34da6a3ce929d0e0e4736.{activityInitializedByW3CHeader.GetSpanId()}.",
                    requestTelemetry.Id);
                Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", requestTelemetry.Context.Operation.Id);
                Assert.Equal("|4bf92f3577b34da6a3ce929d0e0e4736.00f067aa0ba902b7.",
                    requestTelemetry.Context.Operation.ParentId);

                Assert.True(context.Response.Headers.TryGetValue(RequestResponseHeaders.RequestContextHeader,
                    out var appId));
                Assert.Equal($"appId={CommonMocks.TestApplicationId}", appId);
            }
        }

        [Theory]
        [InlineData(AspNetCoreMajorVersion.One)]
        [InlineData(AspNetCoreMajorVersion.Two)]
        public void OnBeginRequestWithW3CHeadersAndRequestIdIsTrackedCorrectly(AspNetCoreMajorVersion aspNetCoreMajorVersion)
        {
            var configuration = TelemetryConfiguration.CreateDefault();
            configuration.TelemetryInitializers.Add(new W3COperationCorrelationTelemetryInitializer());
            using (var hostingListener = new HostingDiagnosticListener(
                CommonMocks.MockTelemetryClient(telemetry => this.sentTelemetry.Enqueue(telemetry), configuration),
                CommonMocks.GetMockApplicationIdProvider(),
                injectResponseHeaders: true,
                trackExceptions: true,
                enableW3CHeaders: true,
                enableNewDiagnosticEvents: aspNetCoreMajorVersion == AspNetCoreMajorVersion.Two))
            {
                hostingListener.OnSubscribe();
                var context = CreateContext(HttpRequestScheme, HttpRequestHost, "/Test", method: "POST");

                context.Request.Headers[RequestResponseHeaders.RequestIdHeader] = "|abc.1.2.3.";
                context.Request.Headers[W3C.W3CConstants.TraceParentHeader] =
                    "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
                context.Request.Headers[W3C.W3CConstants.TraceStateHeader] = "state=some";
                context.Request.Headers[RequestResponseHeaders.CorrelationContextHeader] = "k=v";
                context.Request.Headers[RequestResponseHeaders.RequestContextHeader] = "appId=something";

                HandleRequestBegin(hostingListener, context, 0, aspNetCoreMajorVersion);

                var activityInitializedByW3CHeader = Activity.Current;

                if (aspNetCoreMajorVersion == AspNetCoreMajorVersion.Two)
                {
                    Assert.Equal("|abc.1.2.3.", activityInitializedByW3CHeader.ParentId);
                }

                Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", activityInitializedByW3CHeader.GetTraceId());
                Assert.Equal("00f067aa0ba902b7", activityInitializedByW3CHeader.GetParentSpanId());
                Assert.Equal(16, activityInitializedByW3CHeader.GetSpanId().Length);
                Assert.Equal("state=some", activityInitializedByW3CHeader.GetTracestate());
                Assert.Equal("v", activityInitializedByW3CHeader.Baggage.Single(t => t.Key == "k").Value);

                HandleRequestEnd(hostingListener, context, 0, aspNetCoreMajorVersion);

                Assert.Single(sentTelemetry);
                var requestTelemetry = (RequestTelemetry) this.sentTelemetry.Single();

                Assert.Equal($"|4bf92f3577b34da6a3ce929d0e0e4736.{activityInitializedByW3CHeader.GetSpanId()}.",
                    requestTelemetry.Id);
                Assert.Equal("4bf92f3577b34da6a3ce929d0e0e4736", requestTelemetry.Context.Operation.Id);
                Assert.Equal("|4bf92f3577b34da6a3ce929d0e0e4736.00f067aa0ba902b7.",
                    requestTelemetry.Context.Operation.ParentId);

                Assert.True(context.Response.Headers.TryGetValue(RequestResponseHeaders.RequestContextHeader,
                    out var appId));
                Assert.Equal($"appId={CommonMocks.TestApplicationId}", appId);

                if (aspNetCoreMajorVersion == AspNetCoreMajorVersion.Two)
                {
                    Assert.Equal("abc", requestTelemetry.Properties["ai_legacyRootId"]);
                    Assert.StartsWith("|abc.1.2.3.", requestTelemetry.Properties["ai_legacyRequestId"]);
                }
            }
        }

        [Theory]
        [InlineData(AspNetCoreMajorVersion.One)]
        [InlineData(AspNetCoreMajorVersion.Two)]
        public void OnBeginRequestWithNoW3CHeadersAndRequestIdIsTrackedCorrectly(AspNetCoreMajorVersion aspNetCoreMajorVersion)
        {
            var configuration = TelemetryConfiguration.CreateDefault();
            configuration.TelemetryInitializers.Add(new W3COperationCorrelationTelemetryInitializer());
            using (var hostingListener = new HostingDiagnosticListener(
                CommonMocks.MockTelemetryClient(telemetry => this.sentTelemetry.Enqueue(telemetry), configuration),
                CommonMocks.GetMockApplicationIdProvider(),
                injectResponseHeaders: true,
                trackExceptions: true,
                enableW3CHeaders: true,
                enableNewDiagnosticEvents: aspNetCoreMajorVersion == AspNetCoreMajorVersion.Two))
            {
                hostingListener.OnSubscribe();
                var context = CreateContext(HttpRequestScheme, HttpRequestHost, "/Test", method: "POST");

                context.Request.Headers[RequestResponseHeaders.RequestIdHeader] = "|abc.1.2.3.";
                context.Request.Headers[RequestResponseHeaders.CorrelationContextHeader] = "k=v";

                HandleRequestBegin(hostingListener, context, 0, aspNetCoreMajorVersion);

                var activityInitializedByW3CHeader = Activity.Current;

                Assert.Equal("|abc.1.2.3.", activityInitializedByW3CHeader.ParentId);
                HandleRequestEnd(hostingListener, context, 0, aspNetCoreMajorVersion);

                Assert.Single(sentTelemetry);
                var requestTelemetry = (RequestTelemetry) this.sentTelemetry.Single();

                Assert.Equal(
                    $"|{activityInitializedByW3CHeader.GetTraceId()}.{activityInitializedByW3CHeader.GetSpanId()}.",
                    requestTelemetry.Id);
                Assert.Equal(activityInitializedByW3CHeader.GetTraceId(), requestTelemetry.Context.Operation.Id);
                Assert.Equal("|abc.1.2.3.", requestTelemetry.Context.Operation.ParentId);

                Assert.Equal("abc", requestTelemetry.Properties["ai_legacyRootId"]);
                Assert.StartsWith("|abc.1.2.3.", requestTelemetry.Properties["ai_legacyRequestId"]);
            }
        }

        [Theory]
        [InlineData(AspNetCoreMajorVersion.One)]
        [InlineData(AspNetCoreMajorVersion.Two)]
        public void OnBeginRequestWithW3CSupportAndNoHeadersIsTrackedCorrectly(AspNetCoreMajorVersion aspNetCoreMajorVersion)
        {
            var configuration = TelemetryConfiguration.CreateDefault();
            configuration.TelemetryInitializers.Add(new W3COperationCorrelationTelemetryInitializer());
            using (var hostingListener = new HostingDiagnosticListener(
                CommonMocks.MockTelemetryClient(telemetry => this.sentTelemetry.Enqueue(telemetry), configuration),
                CommonMocks.GetMockApplicationIdProvider(),
                injectResponseHeaders: true,
                trackExceptions: true,
                enableW3CHeaders: true,
                enableNewDiagnosticEvents: aspNetCoreMajorVersion == AspNetCoreMajorVersion.Two))
            {
                hostingListener.OnSubscribe();

                var context = CreateContext(HttpRequestScheme, HttpRequestHost, "/Test", method: "POST");
                context.Request.Headers[RequestResponseHeaders.RequestContextHeader] = "appId=something";

                HandleRequestBegin(hostingListener, context, 0, aspNetCoreMajorVersion);

                var activityInitializedByW3CHeader = Activity.Current;

                Assert.NotNull(activityInitializedByW3CHeader.GetTraceId());
                Assert.Equal(32, activityInitializedByW3CHeader.GetTraceId().Length);
                Assert.Equal(16, activityInitializedByW3CHeader.GetSpanId().Length);
                Assert.Equal(
                    $"00-{activityInitializedByW3CHeader.GetTraceId()}-{activityInitializedByW3CHeader.GetSpanId()}-02",
                    activityInitializedByW3CHeader.GetTraceparent());
                Assert.Null(activityInitializedByW3CHeader.GetTracestate());
                Assert.Empty(activityInitializedByW3CHeader.Baggage);

                HandleRequestEnd(hostingListener, context, 0, aspNetCoreMajorVersion);

                Assert.Single(sentTelemetry);
                var requestTelemetry = (RequestTelemetry) this.sentTelemetry.Single();

                Assert.Equal(
                    $"|{activityInitializedByW3CHeader.GetTraceId()}.{activityInitializedByW3CHeader.GetSpanId()}.",
                    requestTelemetry.Id);
                Assert.Equal(activityInitializedByW3CHeader.GetTraceId(), requestTelemetry.Context.Operation.Id);
                Assert.Null(requestTelemetry.Context.Operation.ParentId);

                Assert.True(context.Response.Headers.TryGetValue(RequestResponseHeaders.RequestContextHeader,
                    out var appId));
                Assert.Equal($"appId={CommonMocks.TestApplicationId}", appId);
            }
        }

        [Theory]
        [InlineData(AspNetCoreMajorVersion.One)]
        [InlineData(AspNetCoreMajorVersion.Two)]
        public void OnBeginRequestWithW3CHeadersAndAppIdInState(AspNetCoreMajorVersion aspNetCoreMajorVersion)
        {
            var configuration = TelemetryConfiguration.CreateDefault();
            configuration.TelemetryInitializers.Add(new W3COperationCorrelationTelemetryInitializer());
            using (var hostingListener = new HostingDiagnosticListener(
                CommonMocks.MockTelemetryClient(telemetry => this.sentTelemetry.Enqueue(telemetry), configuration),
                CommonMocks.GetMockApplicationIdProvider(),
                injectResponseHeaders: true,
                trackExceptions: true,
                enableW3CHeaders: true,
                enableNewDiagnosticEvents: aspNetCoreMajorVersion == AspNetCoreMajorVersion.Two))
            {
                hostingListener.OnSubscribe();

                var context = CreateContext(HttpRequestScheme, HttpRequestHost, "/Test", method: "POST");

                context.Request.Headers[W3C.W3CConstants.TraceParentHeader] =
                    "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-00";
                context.Request.Headers[W3C.W3CConstants.TraceStateHeader] =
                    $"state=some,{W3C.W3CConstants.AzureTracestateNamespace}={ExpectedAppId}";

                HandleRequestBegin(hostingListener, context, 0, aspNetCoreMajorVersion);
                var activityInitializedByW3CHeader = Activity.Current;

                Assert.Equal("state=some", activityInitializedByW3CHeader.GetTracestate());

                HandleRequestEnd(hostingListener, context, 0, aspNetCoreMajorVersion);

                Assert.Single(sentTelemetry);
                var requestTelemetry = (RequestTelemetry) this.sentTelemetry.Single();

                Assert.Equal(ExpectedAppId, requestTelemetry.Source);

                Assert.True(context.Response.Headers.TryGetValue(RequestResponseHeaders.RequestContextHeader,
                    out var appId));
                Assert.Equal($"appId={CommonMocks.TestApplicationId}", appId);
            }
        }

        [Theory]
        [InlineData(AspNetCoreMajorVersion.One)]
        [InlineData(AspNetCoreMajorVersion.Two)]
        public void RequestTelemetryIsProactivelySampledOutIfFeatureFlagIsOn(AspNetCoreMajorVersion aspNetCoreMajorVersion)
        {
            TelemetryConfiguration config = TelemetryConfiguration.CreateDefault();
            config.ExperimentalFeatures.Add("proactiveSampling");
            config.SetLastObservedSamplingPercentage(SamplingTelemetryItemTypes.Request, 0);

            HttpContext context = CreateContext(HttpRequestScheme, HttpRequestHost, "/Test", method: "POST");

            using (var hostingListener = CreateHostingListener(aspNetCoreMajorVersion, config))
            {
                HandleRequestBegin(hostingListener, context, 0, aspNetCoreMajorVersion);

                Assert.NotNull(Activity.Current);

                var requestTelemetry = context.Features.Get<RequestTelemetry>();
                Assert.NotNull(requestTelemetry);
                Assert.Equal(requestTelemetry.Id, Activity.Current.Id);
                Assert.Equal(requestTelemetry.Context.Operation.Id, Activity.Current.RootId);
                Assert.Null(requestTelemetry.Context.Operation.ParentId);
                Assert.True(requestTelemetry.IsSampledOutAtHead);
            }
        }

        [Theory]
        [InlineData(AspNetCoreMajorVersion.One)]
        [InlineData(AspNetCoreMajorVersion.Two)]
        public void RequestTelemetryIsNotProactivelySampledOutIfFeatureFlasIfOff(AspNetCoreMajorVersion aspNetCoreMajorVersion)
        {
            TelemetryConfiguration config = TelemetryConfiguration.CreateDefault();            
            config.SetLastObservedSamplingPercentage(SamplingTelemetryItemTypes.Request, 0);

            HttpContext context = CreateContext(HttpRequestScheme, HttpRequestHost, "/Test", method: "POST");

            using (var hostingListener = CreateHostingListener(aspNetCoreMajorVersion, config))
            {
                HandleRequestBegin(hostingListener, context, 0, aspNetCoreMajorVersion);

                Assert.NotNull(Activity.Current);

                var requestTelemetry = context.Features.Get<RequestTelemetry>();
                Assert.NotNull(requestTelemetry);
                Assert.Equal(requestTelemetry.Id, Activity.Current.Id);
                Assert.Equal(requestTelemetry.Context.Operation.Id, Activity.Current.RootId);
                Assert.Null(requestTelemetry.Context.Operation.ParentId);
                Assert.False(requestTelemetry.IsSampledOutAtHead);
            }
        }

        private void HandleRequestBegin(HostingDiagnosticListener hostingListener, HttpContext context, long timestamp, AspNetCoreMajorVersion aspNetCoreMajorVersion)
        {
            if (aspNetCoreMajorVersion == AspNetCoreMajorVersion.Two)
            {
                if (Activity.Current == null)
                {
                    var activity = new Activity("operation");

                    // Simulating the behaviour of Hosting layer in 2.xx, which parses Request-Id Header and 
                    // set Activity parent.
                    if (context.Request.Headers.TryGetValue("Request-Id", out var requestId))
                    {
                        activity.SetParentId(requestId);
                        if (context.Request.Headers.TryGetValue("Correlation-Context", out var correlationCtx))
                        {
                        }
                    }

                    activity.Start();
                }
                hostingListener.OnHttpRequestInStart(context);
            }
            else
            {
                hostingListener.OnBeginRequest(context, timestamp);
            }
        }

        private void HandleRequestEnd(HostingDiagnosticListener hostingListener, HttpContext context, long timestamp, AspNetCoreMajorVersion aspNetCoreMajorVersion)
        {
            if (aspNetCoreMajorVersion == AspNetCoreMajorVersion.Two)
            {
                hostingListener.OnHttpRequestInStop(context);
            }
            else
            {
                hostingListener.OnEndRequest(context, timestamp);
            }
        }

        private static string FormatTelemetryId(string traceId, string spanId)
        {
            return string.Concat("|", traceId, ".", spanId, ".");
        }

        public void Dispose()
        {
            while (Activity.Current != null)
            {
                Activity.Current.Stop();
            }
        }
    }
}
