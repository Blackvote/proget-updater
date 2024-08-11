package main

import (
	"bytes"
	"context"
	"crypto/sha1"
	"encoding/json"
	"encoding/xml"
	"fmt"
	"github.com/rs/zerolog/log"
	"io"
	"mime/multipart"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"time"
)

func getPackages(ctx context.Context, progetConfig ProgetConfig, timeoutConfig TimeoutConfig) ([]Package, error) {
	var (
		url      string
		packages []Package
		assets,
		allAssets []Asset
	)

	if progetConfig.Type == "asset" {
		url = fmt.Sprintf("%s/endpoints/%s/dir", progetConfig.URL, progetConfig.Feed)
	} else {
		url = fmt.Sprintf("%s/%s/%s/packages", progetConfig.URL, progetConfig.Type, progetConfig.Feed)
	}

	req, err := http.NewRequestWithContext(ctx, "GET", url, nil)
	req.Header.Set("X-ApiKey", progetConfig.APIKey)
	if err != nil {
		return nil, err
	}

	client := &http.Client{
		Timeout: time.Duration(timeoutConfig.IterationTimeout) * time.Second,
	}

	for attempt := 1; attempt <= timeoutConfig.MaxRetries; attempt++ {
		log.Info().Str("url", progetConfig.URL).Str("feed", progetConfig.Feed).Msgf("Attempt %d to get package list", attempt)
		resp, body, err := apiCall(client, req)
		bodyString := string(body)
		log.Debug().Str("url", progetConfig.URL).Str("feed", progetConfig.Feed).Msgf("Get packaget responce body: %s", bodyString)
		if err != nil || resp.StatusCode != http.StatusOK {
			log.Error().Err(err).Str("url", progetConfig.URL).Str("feed", progetConfig.Feed).Msgf("Attempt %d failed to get package. Status: %s", attempt, resp.Status)
			time.Sleep(5 * time.Duration(attempt) * time.Second)
			continue
		}

		if resp.StatusCode == http.StatusOK {
			switch progetConfig.Type {
			case "upack":
				err = json.NewDecoder(strings.NewReader(bodyString)).Decode(&packages)
				if err != nil {
					log.Error().Err(err).Str("url", progetConfig.URL).Str("feed", progetConfig.Feed).Msgf("error decoding package list")
					time.Sleep(5 * time.Duration(attempt) * time.Second)
					continue
				}
			case "nuget":
				packages, err = decodeXML(bodyString)
				if err != nil {
					log.Error().Err(err).Str("url", progetConfig.URL).Str("feed", progetConfig.Feed).Str("feed", progetConfig.Feed).Msgf("error decoding package list")
					time.Sleep(5 * time.Duration(attempt) * time.Second)
					continue
				}
			case "asset":
				err = json.NewDecoder(strings.NewReader(bodyString)).Decode(&assets)
				if err != nil {
					log.Error().Err(err).Str("url", progetConfig.URL).Str("feed", progetConfig.Feed).Msgf("error decoding package list")
					time.Sleep(5 * time.Duration(attempt) * time.Second)
					continue
				}
				for _, asset := range assets {
					if asset.Type == "dir" {
						subAssets, err := fetchAssets(client, url+"/"+asset.Name, asset.Name, progetConfig.APIKey)
						if err != nil {
							return nil, err
						}
						allAssets = append(allAssets, subAssets...)
					} else {
						allAssets = append(allAssets, asset)
					}
				}
				packages = make([]Package, len(allAssets))
				for i, asset := range allAssets {
					packages[i] = Package{
						Group:    "",
						Name:     asset.Name,
						Versions: []string{"0"},
					}
				}
			}

		}

		log.Info().Str("url", progetConfig.URL).Str("feed", progetConfig.Feed).Msgf("Package count: %d", len(packages))
		return packages, nil
	}
	return nil, fmt.Errorf("failed to get package after %d attempts", timeoutConfig.MaxRetries)
}

func getPackagesToSync(config *Config, chain SyncChain, sourcePackages, destPackages []Package) ([]Package, error) {
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
						log.Warn().Str("url", chain.Destination.URL).Str("feed", chain.Destination.Feed).Msgf("%s:%s exceedes version limit, will be processed (dry-run is on)", key, version)
						sourcePackageMap[key][version] = true
					} else {
						sourcePackageMap[key][version] = false
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

	packagesToSyncMap := make(map[string]*Package)
	for _, pkg := range sourcePackages {
		for _, version := range pkg.Versions {
			key := fmt.Sprintf("%s:%s", pkg.Group, pkg.Name)
			if !destPackageMap[key][version] {
				if sourcePackageMap[key][version] {
					log.Printf("%s:%s:%s not found.", pkg.Group, pkg.Name, version)
					if existingPkg, exists := packagesToSyncMap[key]; exists {
						existingPkg.Versions = append(existingPkg.Versions, version)
					} else {
						packagesToSyncMap[key] = &Package{
							Group:    pkg.Group,
							Name:     pkg.Name,
							Versions: []string{version},
						}
					}
				}
			}
		}
	}

	packagesToSync := make([]Package, 0, len(packagesToSyncMap))
	for _, pkg := range packagesToSyncMap {
		packagesToSync = append(packagesToSync, *pkg)
	}

	return packagesToSync, nil
}

func downloadAndUploadPackage(ctx context.Context, config *Config, chain SyncChain, pkg Package, version string, savePath string) error {
	srcParsedURL, err := url.Parse(chain.Source.URL)
	if err != nil {
		return fmt.Errorf("failed to parse url: %s", err)
	}
	srcParseURL := srcParsedURL.Scheme + "://" + srcParsedURL.Host

	dstParsedURL, err := url.Parse(chain.Destination.URL)
	if err != nil {
		return fmt.Errorf("failed to parse url: %s", err)
	}
	dstParseURL := dstParsedURL.Scheme + "://" + dstParsedURL.Host
	var (
		downloadURL,
		uploadURL,
		filePath string
	)

	log.Debug().Str("url", chain.Source.URL).Str("feed", chain.Source.Feed).Msgf("Switch to choose urls. case: %s", chain.Destination.Type)

	switch chain.Type {
	case "upack":
		downloadURL = cleanURL(fmt.Sprintf("%s/%s/%s/download/%s/%s/%s", chain.Source.URL, chain.Type, chain.Source.Feed, pkg.Group, pkg.Name, version))
		uploadURL = cleanURL(fmt.Sprintf("%s/%s/%s/upload", chain.Destination.URL, chain.Type, chain.Destination.Feed))
		filePath = filepath.Join(savePath, fmt.Sprintf("%s.%s.upack", pkg.Name, version))
	case "nuget":
		downloadURL = cleanURL(fmt.Sprintf("%s/%s/%s/package/%s/%s", chain.Source.URL, chain.Type, chain.Source.Feed, pkg.Name, version))
		uploadURL = cleanURL(fmt.Sprintf("%s/%s/%s/upload", chain.Destination.URL, chain.Type, chain.Destination.Feed))
		filePath = filepath.Join(savePath, fmt.Sprintf("%s.%s.nupkg", pkg.Name, version))
	case "asset":
		downloadURL = cleanURL(fmt.Sprintf("%s/endpoints/%s/content/%s", chain.Source.URL, chain.Source.Feed, pkg.Name))
		uploadURL = cleanURL(fmt.Sprintf("%s/endpoints/%s/content/%s", chain.Destination.URL, chain.Destination.Feed, pkg.Name))
		filePath = filepath.Join(savePath, pkg.Name)
	}

	err = os.MkdirAll(savePath, os.ModePerm)
	if err != nil {
		log.Error().Err(err).Msgf("Failed to create dir %s", savePath)
	}

	for attempt := 1; attempt <= config.Timeout.MaxRetries; attempt++ {
		log.Info().Str("url", srcParseURL).Str("feed", chain.Source.Feed).Msgf("Attempt %d download package %s", attempt, pkg.Name)
		err = downloadFile(ctx, downloadURL, filePath, chain.Source, config.Timeout)
		if err != nil {
			log.Error().Err(err).Msgf("Attempt: %d failed", attempt)
			time.Sleep(5 * time.Duration(attempt) * time.Second)
		} else {
			break
		}
		if attempt == config.Timeout.MaxRetries {
			return fmt.Errorf("failed to download %s", filepath.Base(filePath))
		}
	}

	for attempt := 1; attempt <= config.Timeout.MaxRetries; attempt++ {
		log.Info().Str("url", dstParseURL).Str("feed", chain.Destination.Feed).Msgf("Attempt %d upload file %s", attempt, pkg.Name)
		err = uploadFile(ctx, uploadURL, filePath, chain.Destination, config.Timeout)
		if err != nil {
			log.Error().Err(err).Msgf("Attempt: %d failed", attempt)
			time.Sleep(5 * time.Duration(attempt) * time.Second)
		} else {
			break
		}
		if attempt == config.Timeout.MaxRetries {
			return fmt.Errorf("failed to upload %s", filepath.Base(filePath))
		}
	}
	return checkPackageHash(ctx, chain, pkg, version, config.Timeout)
}

func downloadFile(ctx context.Context, URL, filePath string, chain ProgetConfig, timeoutConfig TimeoutConfig) error {
	parsedURL, err := url.Parse(URL)
	if err != nil {
		return fmt.Errorf("failed to parse url: %s", err)
	}
	baseURL := parsedURL.Scheme + "://" + parsedURL.Host

	log.Info().Str("url", baseURL).Str("feed", chain.Feed).Msgf("Download file %s", filepath.Base(filePath))

	req, err := http.NewRequestWithContext(ctx, "GET", URL, nil)
	req.Header.Set("X-ApiKey", chain.APIKey)
	if err != nil {
		return err
	}

	client := &http.Client{
		Timeout: time.Duration(timeoutConfig.WebRequestTimeout) * time.Second,
	}

	resp, err := client.Do(req)
	defer resp.Body.Close()

	if err != nil || resp.StatusCode != http.StatusOK {
		return fmt.Errorf("failed to download %s. Status: %d", filepath.Base(filePath), resp.StatusCode)
	}

	contentType := resp.Header.Get("Content-Type")
	contentLength := resp.Header.Get("Content-Length")

	if !strings.Contains(contentType, "application") {
		return fmt.Errorf("invalid content type: %s", contentType)
	}
	if contentLength == "" || contentLength == "0" {
		return fmt.Errorf("invalid content length: %s", contentLength)
	}

	if resp.StatusCode == http.StatusOK {

		dir := filepath.Dir(filePath)
		err := os.MkdirAll(dir, os.ModePerm)
		if err != nil {
			fmt.Println("Error creating directories:", err)
			return err
		}

		if *debug {
			log.Debug().Str("url", baseURL).Str("feed", chain.Feed).Msgf("creating empty file %s", filePath)
		}

		out, err := os.Create(filePath)
		if err != nil {
			fmt.Println("Error creating file:", err)
			return err
		}

		if *debug {
			log.Debug().Str("url", baseURL).Str("feed", chain.Feed).Msgf("Copy bytes in file %s", filePath)
		}

		hasher := sha1.New()
		multiWriter := io.MultiWriter(out, hasher)

		fileInfo, err := io.Copy(multiWriter, resp.Body)
		if err != nil {
			log.Error().Err(err).Str("url", baseURL).Msgf("Failed to copy response body")
			return err
		}

		out.Close()
		sha1Hash := fmt.Sprintf("%x", hasher.Sum(nil))
		fileSizeMB := float64(fileInfo) / (1024 * 1024)
		log.Info().Str("url", baseURL).Str("feed", chain.Feed).Msgf("Success download %s. File Size: %.2f MB. sha1: %s", strings.TrimPrefix(filePath, "packages\\"), fileSizeMB, sha1Hash)
		return nil
	}
	return err

}

func uploadFile(ctx context.Context, URL, filePath string, chain ProgetConfig, timeoutConfig TimeoutConfig) error {
	parsedURL, err := url.Parse(URL)
	if err != nil {
		return fmt.Errorf("failed to parse url: %s", err)
	}
	baseURL := parsedURL.Scheme + "://" + parsedURL.Host

	log.Info().Str("url", baseURL).Str("feed", chain.Feed).Msgf("Upload package %s", strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"))
	file, err := os.Open(filePath)
	if err != nil {
		return err
	}
	defer file.Close()

	client := &http.Client{
		Timeout: time.Duration(timeoutConfig.WebRequestTimeout) * time.Second,
	}

	log.Debug().Str("url", baseURL).Str("feed", chain.Feed).Msgf("create upload reqeest. File: %s", filepath.Base(filePath))

	var req *http.Request
	if chain.Type == "nuget" {
		var requestBody bytes.Buffer
		writer := multipart.NewWriter(&requestBody)

		part, err := writer.CreateFormFile("package", filepath.Base(filePath))
		if err != nil {
			return fmt.Errorf("failed to create form file: %w", err)
		}

		_, err = io.Copy(part, file)
		if err != nil {
			return fmt.Errorf("failed to copy file: %w", err)
		}

		err = writer.Close()
		if err != nil {
			return fmt.Errorf("failed to close writer: %w", err)
		}

		req, err = http.NewRequestWithContext(ctx, "PUT", URL, &requestBody)
		req.Header.Set("Content-Type", writer.FormDataContentType())
	} else {
		req, err = http.NewRequestWithContext(ctx, "PUT", URL, file)
	}

	req.Header.Add("X-ApiKey", chain.APIKey)
	if err != nil {
		return fmt.Errorf("failed to create request: %w", err)
	}

	resp, err := client.Do(req)
	body, err := io.ReadAll(resp.Body)
	if err != nil {
		log.Error().Err(err).Msgf("Failed to read response body")
	}
	bodyString := string(body)
	log.Debug().Msgf("Upload response body: %s", bodyString)
	defer func(Body io.ReadCloser) {
		err := Body.Close()
		if err != nil {
			log.Error().Err(err).Msgf("Failed to close body")
		}
	}(resp.Body)

	if err != nil || resp.StatusCode != http.StatusCreated {
		return fmt.Errorf("failed to upload %s. Status: %d", filepath.Base(filePath), resp.StatusCode)
	}

	if resp.StatusCode == http.StatusCreated {
		log.Info().Str("url", baseURL).Str("feed", chain.Feed).Msgf("Success upload: for file %s", strings.TrimSuffix(strings.TrimPrefix(filePath, "packages\\"), ".upack"))
		err = os.Remove(filePath)
		return nil
	}

	return err
}

func deleteFile(ctx context.Context, URL, apikey, feed, group, name, version string, timeoutConfig TimeoutConfig) error {
	parsedURL, err := url.Parse(URL)
	if err != nil {
		return fmt.Errorf("failed to parse url: %s", err)
	}
	baseURL := parsedURL.Scheme + "://" + parsedURL.Host

	client := &http.Client{
		Timeout: time.Duration(timeoutConfig.WebRequestTimeout) * time.Second,
	}

	log.Debug().Str("url", baseURL).Str("feed", feed).Msgf("create delete request. File: %s/%s:%s", group, name, version)

	req, err := http.NewRequestWithContext(ctx, "DELETE", baseURL, nil)
	req.Header.Add("X-ApiKey", apikey)
	if err != nil {
		return fmt.Errorf("failed to create request: %w", err)
	}

	resp, err := client.Do(req)
	body, err := io.ReadAll(resp.Body)
	if err != nil {
		log.Error().Err(err).Msgf("Failed to read response body")
	}
	bodyString := string(body)
	log.Debug().Msgf("Delete response body: %s", bodyString)

	defer func(Body io.ReadCloser) {
		err := Body.Close()
		if err != nil {
			log.Error().Err(err).Msgf("Failed to close response body")
		}
	}(resp.Body)
	if err != nil || resp.StatusCode != http.StatusOK {
		return fmt.Errorf("failed to delete %s/%s:%s", group, name, version)
	}

	if resp.StatusCode == http.StatusOK {
		log.Info().Str("url", baseURL).Str("feed", feed).Msgf("Success delete: for file %s/%s:%s", group, name, version)
		return nil
	}
	return err
}

func checkPackageHash(ctx context.Context, chain SyncChain, pkg Package, version string, timeoutConfig TimeoutConfig) error {
	var (
		destHashURL,
		srcHashURL,
		deleteURL string
	)

	switch chain.Type {
	case "upack":
		destHashURL = cleanURL(fmt.Sprintf("%s/%s/%s/versions?group=%s&name=%s&version=%s", chain.Destination.URL, chain.Destination.Type, chain.Destination.Feed, pkg.Group, pkg.Name, version))
		srcHashURL = cleanURL(fmt.Sprintf("%s/%s/%s/versions?group=%s&name=%s&version=%s", chain.Source.URL, chain.Source.Type, chain.Source.Feed, pkg.Group, pkg.Name, version))
		deleteURL = cleanURL(fmt.Sprintf("%s/%s/%s/delete/%s/%s/%s", chain.Destination.URL, chain.Destination.Type, chain.Destination.Feed, pkg.Group, pkg.Name, version))
	case "nuget":
		// have no api to get hash
		log.Warn().Msgf("have no api to check nuget hash")
		return nil
	case "asset":
		destHashURL = cleanURL(fmt.Sprintf("%s/endpoints/%s/metadata/%s", chain.Destination.URL, chain.Destination.Feed, pkg.Name))
		srcHashURL = cleanURL(fmt.Sprintf("%s/endpoints/%s/metadata/%s", chain.Source.URL, chain.Source.Feed, pkg.Name))
		deleteURL = cleanURL(fmt.Sprintf("%s/endpoints/%s/delete/%s", chain.Destination.URL, chain.Destination.Feed, pkg.Name))
	}

	SrcHash, err := getPackageHash(ctx, srcHashURL, chain.Source.APIKey, chain.Source.Feed, pkg.Group, pkg.Name, version, timeoutConfig)
	if err != nil {
		return err
	}

	DestHash, err := getPackageHash(ctx, destHashURL, chain.Destination.APIKey, chain.Destination.Feed, pkg.Group, pkg.Name, version, timeoutConfig)
	if err != nil {
		return err
	}
	if DestHash != SrcHash {
		log.Warn().Msgf("File %s/%s:%s hash does not match, delete it", pkg.Group, pkg.Name, version)
		for attempt := 1; attempt <= timeoutConfig.MaxRetries; attempt++ {
			log.Warn().Msgf("Attempt %d to delete %s/%s:%s", attempt, pkg.Group, pkg.Name, version)
			err := deleteFile(ctx, deleteURL, chain.Destination.APIKey, chain.Destination.Feed, pkg.Group, pkg.Name, version, timeoutConfig)
			if err != nil {
				log.Error().Err(err).Msgf("Failed to delete %s (attempt: %d)", *savePath, attempt)
				time.Sleep(5 * time.Duration(attempt) * time.Second)
			} else {
				break
			}
		}
	}
	log.Warn().Msgf("%s/%s:%s hash match", pkg.Group, pkg.Name, version)
	return nil
}

func getPackageHash(ctx context.Context, URL, apikey, feed, group, name, version string, timeoutConfig TimeoutConfig) (string, error) {
	parsedURL, err := url.Parse(URL)
	if err != nil {
		return "", fmt.Errorf("failed to parse url: %s", err)
	}
	baseURL := parsedURL.Scheme + "://" + parsedURL.Host

	log.Info().Str("url", baseURL).Str("feed", feed).Msgf("Geting hash %s/%s:%s", name, group, version)

	client := &http.Client{
		Timeout: time.Duration(timeoutConfig.WebRequestTimeout) * time.Second,
	}

	req, err := http.NewRequestWithContext(ctx, "GET", URL, nil)
	req.Header.Add("X-ApiKey", apikey)
	if err != nil {
		return "", fmt.Errorf("failed to create request: %w", err)
	}

	for attempt := 1; attempt <= timeoutConfig.MaxRetries; attempt++ {
		log.Info().Str("url", baseURL).Str("feed", feed).Msgf("Attempt %d get hash %s/%s:%s", attempt, name, group, version)
		resp, body, err := apiCall(client, req)
		if err != nil || resp.StatusCode != http.StatusOK {
			log.Error().Err(err).Str("url", baseURL).Str("feed", feed).Msgf("Attempt %d. Failed get hash %s/%s:%s", attempt, name, group, version)
			time.Sleep(5 * time.Duration(attempt) * time.Second)
			continue
		}

		if resp.StatusCode == http.StatusOK {
			var metadata map[string]interface{}
			err = json.Unmarshal(body, &metadata)
			if err != nil {
				return "", fmt.Errorf("failed to unmarshal response body: %w", err)
			}

			pkgSha1, ok := metadata["sha1"].(string)
			if !ok {
				log.Error().Msgf("sha1 key not found or not a string")
				return "", nil
			}

			log.Info().Str("url", baseURL).Str("feed", feed).Msgf("Success get hash %s/%s:%s. sha1: %s", group, name, version, pkgSha1)
			return pkgSha1, nil
		}

		log.Warn().Str("url", baseURL).Str("feed", feed).Msgf("Failed %d attempt. Status Code: %d", attempt, resp.StatusCode)
		time.Sleep(5 * time.Duration(attempt) * time.Second)
		continue
	}
	return "", fmt.Errorf("failed to get hash %s/%s:%s", name, group, version)
}

// gpt-4o
func decodeXML(bodyStr string) ([]Package, error) {
	var packages []Package
	decoder := xml.NewDecoder(strings.NewReader(bodyStr))
	for {
		t, err := decoder.Token()
		if err != nil {
			if err == io.EOF {
				break
			}
			return nil, err
		}
		switch se := t.(type) {
		case xml.StartElement:
			if se.Name.Local == "id" {
				var id string
				err := decoder.DecodeElement(&id, &se)
				if err != nil {
					return nil, err
				}
				parts := strings.Split(id, "Packages(Id='")
				if len(parts) > 1 {
					parts = strings.Split(parts[1], "',Version='")
					if len(parts) > 1 {
						name := parts[0]
						version := strings.TrimSuffix(parts[1], "')")

						found := false
						for i, pkg := range packages {
							if pkg.Name == name {
								packages[i].Versions = append(packages[i].Versions, version)
								found = true
								break
							}
						}
						if !found {
							pkg := Package{
								Group:    "",
								Name:     name,
								Versions: []string{version},
							}
							packages = append(packages, pkg)
						}
					}
				}
			}
		}
	}
	return packages, nil
}

func fetchAssets(client *http.Client, url string, parentPath, apiKey string) ([]Asset, error) {
	var allAssets []Asset

	req, err := http.NewRequest("GET", url, nil)
	if err != nil {
		return nil, err
	}

	req.Header.Add("X-ApiKey", apiKey)

	resp, err := client.Do(req)
	if err != nil {
		return nil, err
	}
	defer func(Body io.ReadCloser) {
		err := Body.Close()
		if err != nil {

		}
	}(resp.Body)

	body, err := io.ReadAll(resp.Body)
	if err != nil {
		return nil, err
	}

	if resp.StatusCode != http.StatusOK {
		return nil, fmt.Errorf("error fetching assets: %v", resp.Status)
	}

	var assets []Asset
	err = json.Unmarshal(body, &assets)
	if err != nil {
		return nil, err
	}

	for _, asset := range assets {
		fullName := parentPath + "/" + asset.Name
		if asset.Type == "dir" {
			subAssets, err := fetchAssets(client, url+"/"+asset.Name, fullName, apiKey)
			if err != nil {
				return nil, err
			}
			allAssets = append(allAssets, subAssets...)
		} else {
			asset.Name = fullName
			allAssets = append(allAssets, asset)
		}
	}

	return allAssets, nil
}

func apiCall(client *http.Client, req *http.Request) (*http.Response, []byte, error) {

	resp, err := client.Do(req)
	if err != nil {
		return nil, nil, err
	}
	log.Debug().Str("url", req.URL.String()).Msgf("get resp %d", resp.StatusCode)
	defer func(Body io.ReadCloser) {
		err := Body.Close()
		if err != nil {
			log.Error().Err(err).Msgf("Failed to close response body")
		}
	}(resp.Body)

	log.Debug().Str("url", req.URL.String()).Msgf("Read body...")
	bodyBytes, err := io.ReadAll(resp.Body)

	return resp, bodyBytes, nil
}
