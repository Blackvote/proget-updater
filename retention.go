package main

import (
	"context"
	"fmt"
	"github.com/rs/zerolog/log"
	"time"
)

func retention(ctx context.Context, config *Config, chain SyncChain, packages []Package) error {

	for _, pkg := range packages {
		if len(pkg.Versions) <= config.Retention.VersionLimit {
			log.Info().Str("url", chain.Destination.URL).Str("feed", chain.Destination.Feed).Str("Action", "Retention").Msgf("package %s have %d version, skip retention", pkg.Name, len(pkg.Versions))
			continue
		}

		for i, version := range pkg.Versions {
			if i > config.Retention.VersionLimit-1 {
				for attempt := 1; attempt <= config.Timeout.MaxRetries; attempt++ {

					var deleteURL string
					switch chain.Type {
					case "upack":
						deleteURL = cleanURL(fmt.Sprintf("%s/api/packages/%s/delete?group=%s&name=%s&version=%s", chain.Destination.URL, chain.Destination.Feed, pkg.Group, pkg.Name, version))
					case "nuget":
						deleteURL = cleanURL(fmt.Sprintf("%s/api/packages/%s/delete?name=%s&version=%s", chain.Destination.URL, chain.Destination.Feed, pkg.Name, version))
					}

					log.Warn().Str("feed", chain.Destination.Feed).Str("Action", "Retention").Msgf("Attempt %d to delete %s/%s:%s", attempt, pkg.Group, pkg.Name, version)
					err, statusCode := deleteFile(ctx, deleteURL, chain.Destination.APIKey, chain.Destination.Feed, pkg.Group, pkg.Name, version, config.Timeout)
					if err != nil {
						log.Error().Err(err).Msgf("Failed to delete %s (attempt: %d)", *savePath, attempt)
						time.Sleep(5 * time.Duration(attempt) * time.Second)
					} else if statusCode == 429 || statusCode == 403 {
						return nil
					} else {
						break
					}
				}
			}
		}
	}
	return nil
}
