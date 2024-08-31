package main

import (
	"fmt"
	"github.com/rs/zerolog"
	"github.com/rs/zerolog/log"
	"gopkg.in/yaml.v2"
	"io"
	"io/ioutil"
	"os"
	"path/filepath"
	"strings"
)

type Config struct {
	SyncChain             []SyncChain     `yaml:"syncChain"`
	Timeout               TimeoutConfig   `yaml:"timeout"`
	ProceedPackageLimit   int             `yaml:"proceedPackageLimit"`
	ProceedPackageVersion int             `yaml:"proceedPackageVersion"`
	Retention             RetentionConfig `yaml:"retention"`
}

type SyncChain struct {
	Source      ProgetConfig `yaml:"source"`
	Destination ProgetConfig `yaml:"destination"`
	Type        string       `yaml:"type"`
}

type ProgetConfig struct {
	URL    string `yaml:"url"`
	APIKey string `yaml:"apiKey"`
	Feed   string `yaml:"feed"`
	Type   string `yaml:"type"`
}

type Package struct {
	Group    string   `yaml:"group"`
	Name     string   `yaml:"name"`
	Versions []string `yaml:"versions"`
}

type Asset struct {
	Name string `yaml:"name"`
	Type string `yaml:"type"`
}

type TimeoutConfig struct {
	WebRequestTimeout int `yaml:"webRequestTimeout"`
	IterationTimeout  int `yaml:"iterationTimeout"`
	SyncTimeout       int `yaml:"syncTimeout"`
	MaxRetries        int `yaml:"maxRetries"`
}

type RetentionConfig struct {
	Enabled      bool `yaml:"enabled"`
	DryRun       bool `yaml:"dry-run"`
	VersionLimit int  `yaml:"versionLimit"`
}

func readConfig(configFile string) (*Config, error) {

	if _, err := os.Stat(configFile); os.IsNotExist(err) {
		return nil, err
	}

	data, err := ioutil.ReadFile(configFile)
	if err != nil {
		return nil, err
	}

	var config Config
	err = yaml.Unmarshal(data, &config)
	if err != nil {
		return nil, err
	}

	for i := range config.SyncChain {
		config.SyncChain[i].Source.URL = strings.TrimSuffix(config.SyncChain[i].Source.URL, "/")
		config.SyncChain[i].Destination.URL = strings.TrimSuffix(config.SyncChain[i].Destination.URL, "/")

		config.SyncChain[i].Source.Type = config.SyncChain[i].Type
		config.SyncChain[i].Destination.Type = config.SyncChain[i].Type
	}
	log.Debug().Msg("Config file read. Validating")

	err = validateConfig(&config)
	if err != nil {
		log.Fatal().Err(err).Msg("Invalid configuration")
	}

	return &config, nil
}

func setupLogging(logFilePath string) (*os.File, error) {
	var logger zerolog.Logger
	var logFile *os.File

	if logFilePath != "" {
		logFile, err := os.OpenFile(logFilePath, os.O_CREATE|os.O_WRONLY|os.O_APPEND, 0666)
		if err != nil {
			return nil, err
		}

		multiWriter := io.MultiWriter(os.Stdout, logFile)
		logger = log.Output(multiWriter).With().Str("app", "Updater").Logger()
	} else {
		Writer := io.Writer(os.Stdout)
		logger = log.Output(Writer).With().Str("app", "Updater").Logger()
	}

	if *debug {
		logger = logger.Level(zerolog.DebugLevel)
	} else {
		logger = logger.Level(zerolog.InfoLevel)
	}

	log.Logger = logger
	log.Debug().Msg("Logger initialized")
	return logFile, nil
}

func createDeleteDirectoryContents(dir string) error {
	log.Debug().Msg("Check for creating temporary packages directory")
	_, err := os.Stat(dir)
	if os.IsNotExist(err) {
		err := os.Mkdir(dir, 0777)
		if err != nil {
			return err
		}
	} else if err != nil {
		return err
	} else {
	}

	log.Debug().Msg("Temporary packages directory exists")
	files, err := ioutil.ReadDir(dir)
	if err != nil {
		return err
	}

	log.Debug().Msgf("Found %d files", len(files))
	for _, file := range files {
		filePath := filepath.Join(dir, file.Name())
		if file.IsDir() {
			err = os.RemoveAll(filePath)
		} else {
			err = os.Remove(filePath)
		}
		if err != nil {
			return err
		}
	}
	log.Debug().Msg("Temporary packages directory removed")
	return nil
}

func cleanURL(url string) string {
	parts := strings.SplitN(url, "://", 2)
	if len(parts) != 2 {
		return url
	}
	parts[1] = strings.ReplaceAll(parts[1], "//", "/")
	return parts[0] + "://" + parts[1]
}

func validateConfig(config *Config) error {
	var errorMessages []string

	if config.ProceedPackageLimit <= 0 {
		errorMessages = append(errorMessages, "invalid ProceedPackageLimit: must be greater than 0")
	}
	if config.ProceedPackageVersion <= 0 {
		errorMessages = append(errorMessages, "invalid ProceedPackageVersion: must be greater than 0")
	}

	if config.Timeout.SyncTimeout <= 0 {
		errorMessages = append(errorMessages, "invalid SyncTimeout: must be greater than 0")
	}
	if config.Timeout.IterationTimeout <= 0 {
		errorMessages = append(errorMessages, "invalid IterationTimeout: must be greater than 0")
	}
	if config.Timeout.WebRequestTimeout <= 0 {
		errorMessages = append(errorMessages, "invalid WebRequestTimeout: must be greater than 0")
	}
	if config.Timeout.MaxRetries <= 0 {
		errorMessages = append(errorMessages, "invalid MaxRetries: must be greater than 0")
	}

	if len(config.SyncChain) <= 0 {
		errorMessages = append(errorMessages, fmt.Sprintf("found 0 syncChains"))
	}
	for i, chain := range config.SyncChain {
		if chain.Source.URL == "" {
			errorMessages = append(errorMessages, fmt.Sprintf("source URL cannot be empty for chain %d", i+1))
		}
		if chain.Destination.URL == "" {
			errorMessages = append(errorMessages, fmt.Sprintf("destination URL cannot be empty for chain %d", i+1))
		}
		if chain.Source.APIKey == "" {
			errorMessages = append(errorMessages, fmt.Sprintf("source API key cannot be empty for chain %d", i+1))
		}
		if chain.Destination.APIKey == "" {
			errorMessages = append(errorMessages, fmt.Sprintf("destination API key cannot be empty for chain %d", i+1))
		}
	}

	if config.Retention.Enabled && config.Retention.VersionLimit <= 0 {
		errorMessages = append(errorMessages, "invalid VersionLimit for retention: must be greater than 0")
	}

	if len(errorMessages) > 0 {
		return fmt.Errorf("configuration validation errors: %s", strings.Join(errorMessages, ";"))
	}
	log.Debug().Msg("Configuration validation successful")
	return nil
}
