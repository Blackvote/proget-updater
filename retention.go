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
			log.Debug().Str("url", chain.Destination.URL).Str("feed", chain.Destination.Feed).Str("Action", "Retention").Msgf("package %s have %d version, skip retention", pkg.Name, len(pkg.Versions))
			continue
		}

		log.Info().Str("url", chain.Destination.URL).Str("feed", chain.Destination.Feed).Str("Action", "Retention").Msgf("package %s have %d version, retention", pkg.Name, len(pkg.Versions))
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

					switch statusCode {
					case 429:
						log.Info().Str("feed", chain.Destination.Feed).Str("Action", "Delete").Msgf("Delete reqest rate limit was exeed. Skip retention")
						return nil
					case 403:
						log.Info().Str("feed", chain.Destination.Feed).Str("Action", "Delete").Msgf("Add \"delete\" permission to apiKey")
						return nil
					}
					if err != nil {
						log.Error().Err(err).Msgf("Failed to delete %s (attempt: %d)", *savePath, attempt)
						time.Sleep(5 * time.Duration(attempt) * time.Second)
					} else {
						break
					}
					if attempt == config.Timeout.MaxRetries {
						return fmt.Errorf("failed to delete %s", *savePath)
					}
				}
			}
		}
	}
	return nil
}
