# OpenAPI Reference

This page renders the checked-in Query API OpenAPI schema. The contract is
written for both humans and tooling: it documents bounded telemetry access,
explicit validation limits, RFC 9457-style problem responses, analytical
request shapes, open-ended known-value lists, and replay-oriented response
models.

Use the schema as the source for generated API references, contract checks,
client prototypes, and future AI documentation-agent workflows.

[Download the schema](openapi.yaml)

<script type="module" src="https://unpkg.com/rapidoc/dist/rapidoc-min.js"></script>

<rapi-doc
  spec-url="../openapi.yaml"
  render-style="read"
  layout="column"
  show-header="false"
  show-info="true"
  allow-try="false"
  allow-spec-url-load="false"
  allow-spec-file-load="false"
  schema-style="table"
  regular-font="system-ui, -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif"
  mono-font="SFMono-Regular, Consolas, 'Liberation Mono', Menlo, monospace"
></rapi-doc>
