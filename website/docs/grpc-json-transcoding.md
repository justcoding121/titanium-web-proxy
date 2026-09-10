# gRPC-JSON transcoding

Call a gRPC service with ordinary HTTP and JSON — Plus maps REST/JSON to gRPC (and back) using `google.api.http` annotations in a compiled protobuf FileDescriptorSet.

**Next:** enable Plus → point at a descriptor set → hit the annotated HTTP paths with `curl` or any HTTP client.

## Requirements

- Plus enabled (`plus.enabled: true`) with Plus installed
- A FileDescriptorSet (`.pb`) built with imports
- Fully-qualified service names listed in config
- HTTP/2 to the gRPC origin (enable HTTP/2 on the listener / server / cluster path)

When disabled, Core does not call into the transcoder (null check only — no body buffering).

Enabling the feature forces the HTTP session interception path for that process (same class of cost as route transforms or WAF).

## Annotate services

```protobuf
syntax = "proto3";
package helloworld;
import "google/api/annotations.proto";

service Greeter {
  rpc SayHello (HelloRequest) returns (HelloReply) {
    option (google.api.http) = {
      get: "/v1/greeter/{name}"
    };
  }
  rpc CreateHello (HelloRequest) returns (HelloReply) {
    option (google.api.http) = {
      post: "/v1/greeter"
      body: "*"
    };
  }
}

message HelloRequest { string name = 1; }
message HelloReply { string message = 1; }
```

## Build a descriptor set

```shell
protoc -I${GOOGLEAPIS} -I. \
  --include_imports --include_source_info \
  --descriptor_set_out=api_descriptor.pb \
  your_service.proto
```

Include `google/api/annotations.proto` (and its imports) on the include path.

## CLI config

```yaml
plus:
  enabled: true
  controlPlane:
    host: "127.0.0.1"
    port: 9080
    sharedSecret: "<shared-secret>"
  options:
    grpc.transcode.enabled: "true"
    grpc.transcode.descriptorSet: "./protos/api_descriptor.pb"
    grpc.transcode.services: "helloworld.Greeter"
    grpc.transcode.convertGrpcStatus: "true"
    grpc.transcode.ignoreUnknownQueryParameters: "true"
    grpc.transcode.preserveProtoFieldNames: "false"
    grpc.transcode.alwaysPrintPrimitiveFields: "false"
    grpc.transcode.compression: "gzip"

listeners:
  - name: main
    port: 8080
    enableHttp2: true

routes:
  - match: { pathPrefix: "/" }
    cluster: greeter

clusters:
  - name: greeter
    destinations:
      - address: "https://127.0.0.1:50051"
```

Startup fails if the feature is enabled but the descriptor file is missing or `services` is empty.

## Behavior

| Client | Upstream |
|--------|----------|
| REST verb + path from `google.api.http` | `POST /package.Service/Method` |
| `application/json` (or empty GET body) | `application/grpc` length-prefixed protobuf |
| HTTP status from `grpc-status` | Trailers `grpc-status` / `grpc-message` |

Unmatched REST requests pass through unchanged. Against a gRPC-only origin that usually fails — keep routes scoped to mapped prefixes.

Supports **unary** and **multi-frame** responses (server streaming frames become a JSON array). Optional **gzip** compression via `grpc.transcode.compression` (`true` or `gzip`).

### Example

```shell
curl -s http://127.0.0.1:8080/v1/greeter/world
# {"message":"Hello world"}
```

## Inspector

Transcoded sessions set `IsTranscoded` and support search `is:transcoded`. The inspect pane shows client REST/JSON faces and upstream gRPC path / framed payloads.

## See also

- [Plus](/docs/plus)
- [Configuration](/docs/configuration)
- [Inspector](/docs/inspector)
- [Performance](/docs/performance)
