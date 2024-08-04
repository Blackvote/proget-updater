package main

import (
	"context"
	"fmt"
	"github.com/rs/zerolog/log"
	"net/http"
	"time"
)

func retention(ctx context.Context, config *Config, chain SyncChain, packages []Package) error {
	client := &http.Client{
		Timeout: time.Duration(config.Timeout.WebRequestTimeout) * time.Second,
	}

	for _, pkg := range packages {
		if len(pkg.Versions) <= config.Retention.VersionLimit {
			log.Info().Str("url", chain.Destination.URL).Msgf("package %s have %d version, skip retention", pkg.Name, len(pkg.Versions))
			continue
		}

		for i, version := range pkg.Versions {
			if i > config.Retention.VersionLimit-1 {
				deleteURL := fmt.Sprintf("%s/upack/%s/delete/%s/%s/%s", chain.Destination.URL, chain.Destination.Feed, pkg.Group, pkg.Name, version)
				log.Info().Str("url", chain.Destination.URL).Msgf("Package: %s/%s:%s mark for delete", pkg.Group, pkg.Name, version)
				if !config.Retention.DryRun {
					req, err := http.NewRequestWithContext(ctx, "POST", deleteURL, nil)
					if err != nil {
						log.Error().Str("url", chain.Destination.URL).Msgf("Failed to create reqeust for %s", deleteURL)
						continue
					}
					req.Header.Set("X-ApiKey", chain.Destination.APIKey)
					for attempt := 1; attempt <= config.Timeout.MaxRetries; attempt++ {
						log.Info().Str("url", chain.Destination.URL).Msgf("Attempt %d to delete %s/%s:%s", attempt, pkg.Group, pkg.Name, version)

						resp, _, err := apiCall(client, req)
						if err != nil || resp.StatusCode != http.StatusOK {
							log.Error().Str("url", chain.Destination.URL).Msgf("Failed to delete %s/%s:%s. Error: %s", pkg.Group, pkg.Name, version, resp.Status)
							continue
						}

						if resp.StatusCode == http.StatusOK {
							log.Info().Str("url", chain.Destination.URL).Msgf("Success delete %s/%s:%s", pkg.Group, pkg.Name, version)
							return nil
						}

						log.Warn().Str("url", chain.Destination.URL).Msgf("Failed %d attempt. Status Code: %d.", attempt, resp.StatusCode)
						time.Sleep(5 * time.Duration(attempt) * time.Second)
						continue
					}
				} else {
					log.Info().Str("url", chain.Destination.URL).Msgf("Skip delete: %s/%s:%s (dry-run is on)", pkg.Group, pkg.Name, version)
				}
			}
		}
	}
	return nil
}
