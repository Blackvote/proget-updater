package main

import (
	"context"
	"fmt"
	"github.com/rs/zerolog/log"
	"net/http"
	"time"
)

func retention(ctx context.Context, config *Config, chain SyncChain, packages []Package) error {
	for _, pkg := range packages {
		if len(pkg.Versions) <= config.Retention.VersionLimit {
			log.Info().Str("url", chain.Destination.URL).Msgf("Version limit less than version, skip retention")
			continue
		}

		var resp *http.Response
		for i, version := range pkg.Versions {
			if i > config.Retention.VersionLimit-1 {
				deleteURL := fmt.Sprintf("%s/upack/%s/delete/%s/%s/%s", chain.Destination.URL, chain.Destination.Feed, pkg.Group, pkg.Name, version)
				log.Info().Str("url", deleteURL).Msgf("Package: %s/%s:%s mark for delete", pkg.Group, pkg.Name, version)
				if !config.Retention.DryRun {
					req, err := http.NewRequestWithContext(ctx, "POST", deleteURL, nil)
					if err != nil {
						log.Error().Str("url", deleteURL).Msgf("Failed to create reqeust for %s", deleteURL)
						continue
					}

					req.Header.Set("X-ApiKey", chain.Destination.APIKey)
					client := &http.Client{}

					for attempt := 1; attempt <= maxRetries; attempt++ {
						log.Info().Str("url", deleteURL).Msgf("Attempt %d to delete %s/%s:%s", attempt, pkg.Group, pkg.Name, version)

						resp, err := client.Do(req)
						if err == nil && resp.StatusCode == http.StatusOK {
							resp.Body.Close()
							log.Info().Str("url", deleteURL).Msgf("Success delete %s/%s:%s", pkg.Group, pkg.Name, version)
							break
						}

						time.Sleep(3 * time.Duration(attempt) * time.Second)
					}
					if resp != nil {
						log.Error().Str("url", deleteURL).Msgf("Failed to delete %s/%s:%s. Status: %s", pkg.Group, pkg.Name, version, resp.Status)
						resp.Body.Close()
					}

					if err != nil {
						log.Error().Str("url", deleteURL).Msgf("Failed to delete %s/%s:%s. Error: %s", pkg.Group, pkg.Name, version, resp.Status)
					}
				} else {
					log.Info().Str("url", deleteURL).Msgf("Skip delete: %s/%s:%s (dry-run is on)", pkg.Group, pkg.Name, version)
				}
			}
		}
	}
	return nil
}
