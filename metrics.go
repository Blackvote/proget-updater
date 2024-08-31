package main

import (
	"github.com/prometheus/client_golang/prometheus"
)

var (
	HttpRequestsTotal = prometheus.NewGaugeVec(
		prometheus.GaugeOpts{
			Name: "updater_http_requests_total",
			Help: "Total number of HTTP requests by one loop categorized by status code and HTTP method.",
		},
		[]string{"action", "code", "method"},
	)

	PackageProceedTotal = prometheus.NewGaugeVec(
		prometheus.GaugeOpts{
			Name: "updater_package_proceed_total",
			Help: "Total number of package successfully proceeded by one loop.",
		},
		[]string{"feed"},
	)
)
