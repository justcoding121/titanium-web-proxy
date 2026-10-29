// Package conformance runs the upstream Gateway API conformance suite against Titanium Gateway.
//
// The suite reads its settings from flags (--gateway-class, --conformance-profiles, --supported-features,
// --exempt-features, --report-output, --organization, --project, --url, --version, --contact), which
// tools/gateway-conformance/run.sh sets. Run that script instead of `go test` directly: the suite needs a cluster with
// the chart installed and a LoadBalancer address for the data plane.
package conformance

import (
	"testing"

	"sigs.k8s.io/gateway-api/conformance"
)

func TestConformance(t *testing.T) {
	conformance.RunConformance(t)
}
