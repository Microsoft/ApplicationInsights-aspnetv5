﻿namespace SampleWebAppIntegration.FunctionalTest
{
    using System;
    using FunctionalTestUtils;
    using Microsoft.ApplicationInsights.DataContracts;
    using Xunit;

    public class ExceptionTelemetryWebApiTests : TelemetryTestsBase
    {
        private const string assemblyName = "WebApiShimFw46.FunctionalTests";

        [Fact]
        public void TestBasicRequestPropertiesAfterRequestingControllerThatThrows()
        {
            using (var server = new InProcessServer(assemblyName))
            {
                const string RequestPath = "/api/exception";

                var expectedRequestTelemetry = new RequestTelemetry();
                expectedRequestTelemetry.Name = "GET Exception/Get";
                expectedRequestTelemetry.ResponseCode = "500";
                expectedRequestTelemetry.Success = false;
                expectedRequestTelemetry.Url = new System.Uri(server.BaseHost + RequestPath);

                this.ValidateBasicRequest(server, RequestPath, expectedRequestTelemetry);
            }
        }

        [Fact]
        public void TestBasicExceptionPropertiesAfterRequestingControllerThatThrows()
        {
            using (var server = new InProcessServer(assemblyName))
            {
                var expectedExceptionTelemetry = new ExceptionTelemetry();
                expectedExceptionTelemetry.Exception = new InvalidOperationException();

                this.ValidateBasicException(server, "/api/exception", expectedExceptionTelemetry);
            }
        }
    }
}
