package main

import (
	"context"
	"encoding/json"
	"fmt"
	"github.com/rs/zerolog/log"
	"io"
	"net/http"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

func downloadAndUploadPackage(ctx context.Context, config *Config, chain SyncChain, pkg Package, version, savePath string) error {
	downloadURL := fmt.Sprintf("%s/upack/%s/download/%s/%s/%s", chain.Source.URL, chain.Source.Feed, pkg.Group, pkg.Name, version)
	uploadURL := fmt.Sprintf("%s/upack/%s/upload", chain.Destination.URL, chain.Destination.Feed)

	err := os.MkdirAll(savePath, os.ModePerm)
	if err != nil {
		log.Error().Msgf("Failed to create dir %s", savePath)
	}

	filePath := filepath.Join(savePath, fmt.Sprintf("%s.%s.upack", pkg.Name, version))

	err = downloadFile(ctx, downloadURL, chain.Source.APIKey, filePath, config.Timeout)
	if err != nil {
		return err
	}

	return uploadFile(ctx, uploadURL, chain.Destination.APIKey, filePath, config.Timeout)
}

func getPackages(ctx context.Context, progetConfig ProgetConfig, timeoutConfig TimeoutConfig) ([]Package, error) {
	url := fmt.Sprintf("%s/upack/%s/packages", progetConfig.URL, progetConfig.Feed)
	req, err := http.NewRequestWithContext(ctx, "GET", url, nil)
	if err != nil {
		return nil, err
	}

	req.Header.Set("X-ApiKey", progetConfig.APIKey)
	client := &http.Client{
		Timeout: time.Duration(timeoutConfig.IterationTimeout) * time.Second,
	}

	var resp *http.Response
	var packages []Package

	for attempt := 1; attempt <= maxRetries; attempt++ {
		log.Info().Str("url", url).Msgf("Attempt %d to get package list", attempt)
		resp, err = client.Do(req)
		if err == nil && resp.StatusCode == http.StatusOK {
			err = json.NewDecoder(resp.Body).Decode(&packages)
			resp.Body.Close()
			if err != nil {
				log.Error().Str("url", url).Msgf("Attempt %d failed to decode answer. Error: %s", attempt, err)
				time.Sleep(3 * time.Duration(attempt) * time.Second)
				continue
			}

			log.Info().Str("url", url).Msgf("Package count in %s/%s: %d", progetConfig.URL, progetConfig.Feed, len(packages))

			var packageList strings.Builder
			for _, pkg := range packages {
				packageList.WriteString(fmt.Sprintf("%s/%s: %s | ", pkg.Group, pkg.Name, strings.Join(pkg.Versions, " ")))
			}
			log.Info().Str("url", url).Msg(packageList.String())

			return packages, nil
		}
		time.Sleep(3 * time.Duration(attempt) * time.Second)
	}
	if err != nil {
		log.Error().Str("url", url).Msgf("Failed to get package, Error: %s", err)
	}
	if resp != nil && err == nil {
		resp.Body.Close()
		log.Error().Str("url", url).Msgf("Failed to get package, Status: %s", resp.Status)
	}
	return nil, fmt.Errorf("failed to get package after %d attempts", maxRetries)
}

func SyncPackages(ctx context.Context, config *Config, chain SyncChain, sourcePackages, destPackages []Package, savePath string) error {
	sourcePackageMap := make(map[string]map[string]bool)
	for _, pkg := range sourcePackages {
		key := fmt.Sprintf("%s:%s", pkg.Group, pkg.Name)
		if sourcePackageMap[key] == nil {
			sourcePackageMap[key] = make(map[string]bool)
		}
		for i, version := range pkg.Versions {
			if !config.Retention.Enabled {
				sourcePackageMap[key][version] = true
			} else {
				if i <= config.Retention.VersionLimit-1 {
					sourcePackageMap[key][version] = true
				} else {
					if !config.Retention.DryRun {
						sourcePackageMap[key][version] = false
					} else {
						log.Warn().Str("url", chain.Destination.URL).Msgf("%s:%s exceedes version limit, will be processed (dry-run is on)", key, version)
						sourcePackageMap[key][version] = true
					}
				}
			}
		}
	}

	destPackageMap := make(map[string]map[string]bool)
	for _, pkg := range destPackages {
		key := fmt.Sprintf("%s:%s", pkg.Group, pkg.Name)
		if destPackageMap[key] == nil {
			destPackageMap[key] = make(map[string]bool)
		}
		for _, version := range pkg.Versions {
			destPackageMap[key][version] = true
		}
	}

	if len(sourcePackageMap) < config.ProceedPackageLimit {
		config.ProceedPackageLimit = len(sourcePackageMap)
	}
	semaphore := make(chan struct{}, config.ProceedPackageLimit)
	var wg sync.WaitGroup
	for _, pkg := range sourcePackages {
		for _, version := range pkg.Versions {
			key := fmt.Sprintf("%s:%s", pkg.Group, pkg.Name)
			if !destPackageMap[key][version] {
				if sourcePackageMap[key][version] {
					log.Info().Str("url", chain.Destination.URL).Msgf("%s:%s:%s not found. Syncing", pkg.Group, pkg.Name, version)
					wg.Add(1)
					go func(pkg Package, version string) {
						defer wg.Done()
						semaphore <- struct{}{}
						defer func() { <-semaphore }()
						err := downloadAndUploadPackage(ctx, config, chain, pkg, version, savePath)
						if err != nil {
							log.Error().Err(err).Str("url", chain.Destination.URL).Msgf("Failed to Sync package %s:%s", pkg.Name, version)
						}
					}(pkg, version)
				}
			} else {
				log.Info().Str("url", chain.Destination.URL).Msgf("%s:%s:%s found.", pkg.Group, pkg.Name, version)
			}
		}
	}
	wg.Wait()
	return nil
}

func downloadFile(ctx context.Context, url, apiKey, filePath string, timeoutConfig TimeoutConfig) error {
	log.Info().Str("url", url).Msgf("Download package %s", filePath)

	req, err := http.NewRequestWithContext(ctx, "GET", url, nil)
	if err != nil {
		return err
	}
	req.Header.Set("X-ApiKey", apiKey)
	client := &http.Client{
		Timeout: time.Duration(timeoutConfig.WebRequestTimeout) * time.Second,
	}

	var resp *http.Response
	for attempt := 1; attempt <= maxRetries; attempt++ {
		log.Info().Str("url", url).Msgf("Attempt download %d file %s", attempt, filePath)

		resp, err = client.Do(req)
		if err == nil && resp.StatusCode == http.StatusOK {
			out, err := os.Create(filePath)

			if err != nil {
				log.Error().Err(err).Str("url", url).Msgf("Failed to create file")
				time.Sleep(3 * time.Duration(attempt) * time.Second)
				continue
			}

			fileSize, err := io.Copy(out, resp.Body)
			if err != nil {
				log.Error().Err(err).Str("url", url).Msgf("Failed to copy response body")
				continue
			}

			out.Close()
			fileSizeMB := float64(fileSize) / (1024 * 1024)
			log.Info().Str("url", url).Msgf("%s file Size: %.2f MB", strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"), fileSizeMB)
			log.Info().Str("url", url).Msgf("Success download %s", strings.TrimPrefix(filePath, "packages\\"))
			return nil
		}

		time.Sleep(3 * time.Duration(attempt) * time.Second)
	}
	if resp != nil {
		resp.Body.Close()
		log.Error().Str("url", url).Msgf("Failed download, Status: %s", resp.Status)
	}

	if err != nil {
		log.Error().Str("url", url).Msgf("Failed download for file %s, Error: %s", strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"), err)
	}
	return fmt.Errorf("failed to download %s after %d attempts", strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"), maxRetries)
}

func uploadFile(ctx context.Context, url, apiKey, filePath string, timeoutConfig TimeoutConfig) error {
	log.Info().Str("url", url).Msgf("Upload package %s", strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"))
	file, err := os.Open(filePath)
	if err != nil {
		return err
	}
	defer file.Close()

	req, err := http.NewRequestWithContext(ctx, "POST", url, file)
	if err != nil {
		return err
	}
	req.Header.Set("X-ApiKey", apiKey)
	client := &http.Client{
		Timeout: time.Duration(timeoutConfig.WebRequestTimeout) * time.Second,
	}

	var resp *http.Response
	for attempt := 1; attempt <= maxRetries; attempt++ {
		log.Info().Str("url", url).Msgf("Attempt %d upload for file %s", attempt, strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"))

		resp, err := client.Do(req)
		if err == nil && resp.StatusCode == http.StatusCreated {
			log.Info().Str("url", url).Msgf("Success upload: for file %s", strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"))
			err = os.Remove(filePath)
			return nil
		}

		time.Sleep(3 * time.Duration(attempt) * time.Second)
	}
	if resp != nil {
		resp.Body.Close()
		log.Error().Str("url", url).Msgf("Failed upload  file %s, Status: %s", strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"), resp.Status)
	}

	if err != nil {
		log.Error().Str("url", url).Msgf("Failed upload file %s, Error: %s", strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"), err)
	}
	return fmt.Errorf("failed to upload %s after %d attempts", strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"), maxRetries)
}
