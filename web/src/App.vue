<template>
  <div v-if="config" id="app" :class="[
    `theme-${config.theme}`,
    `page-${currentPage}`,
    isDark ? 'dark' : 'light',
    !config.footer ? 'no-footer' : '',
  ]">
    <DynamicTheme v-if="config.colors" :themes="config.colors" />
    <div id="bighead">
      <section v-if="config.header" class="first-line">
        <div v-cloak class="container">
          <div class="logo">
            <a href="#">
              <img v-if="config.logo" :src="config.logo" alt="dashboard logo" />
            </a>
            <i v-if="config.icon" :class="config.icon"></i>
          </div>
          <div class="dashboard-title">
            <span class="headline">{{ config.subtitle }}</span>
            <h1>{{ config.title }}</h1>
          </div>
        </div>
      </section>

      <Navbar :open="showMenu" :links="config.links" @navbar-toggle="showMenu = !showMenu">
        <DarkMode :default-value="config.defaults.colorTheme" @updated="isDark = $event" />

        <SettingToggle name="vlayout" icon="fa-list" icon-alt="fa-columns"
          :default-value="config.defaults.layout == 'columns'" @updated="vlayout = $event" />

        <SearchInput class="navbar-item" :hotkey="searchHotkey()" @input="filterServices($event)"
          @search-focus="showMenu = true" @search-open="navigateToFirstService" @search-cancel="filterServices()" />
      </Navbar>
    </div>

    <section id="main-section" class="section">
      <div v-cloak class="container">
        <!-- <ConnectivityChecker
          v-if="config.connectivityCheck"
          @network-status-update="offline = $event"
        /> -->

        <GetStarted v-if="configurationNeeded" />

        <div v-if="!offline">
          <!-- Optional messages -->
          <Message :item="config.message" />

          <!-- Horizontal layout -->
          <div v-if="!vlayout || filter" class="columns is-multiline">
            <template v-for="(group, groupIndex) in services">
              <h2 v-if="group.name" :key="`header-${groupIndex}`" class="column is-full group-title"
                :class="group.class">
                <i v-if="group.icon" :class="['fa-fw', group.icon]"></i>
                <div v-else-if="group.logo" class="group-logo media-left">
                  <figure class="image is-48x48">
                    <img :src="group.logo" :alt="`${group.name} logo`" />
                  </figure>
                </div>
                {{ group.name }}
              </h2>
              <Service v-for="(item, index) in group.items" :key="`service-${groupIndex}-${index}`" :item="item"
                :proxy="config.proxy" :class="[
                  'column',
                  `is-${12 / config.columns}`,
                  `${item.class || group.class || ''}`,
                ]" />
            </template>
          </div>

          <!-- Vertical layout -->
          <div v-if="!filter && vlayout" class="columns is-multiline layout-vertical">
            <div v-for="(group, groupIndex) in services" :key="groupIndex"
              :class="['column', `is-${12 / config.columns}`]">
              <h2 v-if="group.name" class="group-title" :class="group.class">
                <i v-if="group.icon" :class="['fa-fw', group.icon]"></i>
                <div v-else-if="group.logo" class="group-logo media-left">
                  <figure class="image is-48x48">
                    <img :src="group.logo" :alt="`${group.name} logo`" />
                  </figure>
                </div>
                {{ group.name }}
              </h2>
              <Service v-for="(item, index) in group.items" :key="index" :item="item" :proxy="config.proxy"
                :class="item.class || group.class" />
            </div>
          </div>
        </div>
      </div>
    </section>

    <footer class="footer">
      <div class="container">
        <div v-if="config.footer" class="content has-text-centered" v-html="config.footer"></div>
      </div>
    </footer>
  </div>
</template>

<script>
import { parse } from "yaml";
import merge from "lodash.merge";

import Navbar from "./components/Navbar.vue";
import GetStarted from "./components/GetStarted.vue";
import ConnectivityChecker from "./components/ConnectivityChecker.vue";
import Service from "./components/Service.vue";
import Message from "./components/Message.vue";
import SearchInput from "./components/SearchInput.vue";
import SettingToggle from "./components/SettingToggle.vue";
import DarkMode from "./components/DarkMode.vue";
import DynamicTheme from "./components/DynamicTheme.vue";

import defaultConfig from "./assets/defaults.yml?raw";

export default {
  name: "App",
  components: {
    Navbar,
    GetStarted,
    ConnectivityChecker,
    Service,
    Message,
    SearchInput,
    SettingToggle,
    DarkMode,
    DynamicTheme,
  },
  data: function () {
    return {
      loaded: false,
      currentPage: null,
      configNotFound: false,
      config: null,
      services: null,
      offline: false,
      filter: "",
      vlayout: true,
      isDark: null,
      showMenu: false,
      configInterval: null, // store interval ID
      configErrorCount: 0, // track consecutive config errors
    };
  },
  computed: {
    configurationNeeded: function () {
      return (this.loaded && !this.services) || this.configNotFound;
    },
  },
  created: async function () {
    this.buildDashboard();
    window.onhashchange = this.buildDashboard;
    this.loaded = true;
    // Set up polling and visibility listeners
    this.startConfigPolling();
    document.addEventListener('visibilitychange', this.handleVisibilityChange);
  },
  beforeDestroy() {
    // Clear the config polling interval and event listeners when component is destroyed
    this.stopConfigPolling();
    document.removeEventListener('visibilitychange', this.handleVisibilityChange);
  },
  methods: {
    startConfigPolling() {
      if (!this.configInterval) {
        this.configInterval = setInterval(() => {
          this.buildDashboard();
        }, 500);
      }
    },
    stopConfigPolling() {
      if (this.configInterval) {
        clearInterval(this.configInterval);
        this.configInterval = null;
      }
    },
    handleVisibilityChange() {
      if (document.hidden) {
        this.stopConfigPolling();
      } else {
        this.startConfigPolling();
      }
    },
    searchHotkey() {
      if (this.config.hotkey && this.config.hotkey.search) {
        return this.config.hotkey.search;
      }
    },
    buildDashboard: async function () {
      const defaults = parse(defaultConfig);
      let config;
      let errorOccurred = false;
      try {
        config = await this.getConfig();
        this.currentPage = window.location.hash.substring(1) || "default";

        if (this.currentPage !== "default") {
          let pageConfig = await this.getConfig(
            `assets/${this.currentPage}.yml`,
          );
          config = Object.assign(config, pageConfig);
        }
        // Success: reset error counter
        this.configErrorCount = 0;
        this.configNotFound = false;
      } catch (error) {
        console.log(error);
        this.configErrorCount = (this.configErrorCount || 0) + 1;
        errorOccurred = true;
      }
      // Only show error if 6 or more consecutive failures
      if (errorOccurred && this.configErrorCount >= 6) {
        config = this.handleErrors("⚠️ Error loading configuration", "Failed to load config 6 times in a row.");
        this.configNotFound = true;
        console.log("Failed to load config >6 times in a row");
      } else if (errorOccurred) {
        // Swallow error: don't update config/services, just return
        console.log("Swallow error: don't update config/services, just return");
        return;
      } else {
        this.configNotFound = false;
      }
      this.config = merge(defaults, config);
      this.services = this.config.services;

      // Re-apply search filter if set
      if (this.filter) {
        this.filterServices(this.filter);
      }

      document.title =
        this.config.documentTitle ||
        `${this.config.title} | ${this.config.subtitle}`;
      if (this.config.stylesheet) {
        let stylesheet = "";
        let addtionnal_styles = this.config.stylesheet;
        if (!Array.isArray(this.config.stylesheet)) {
          addtionnal_styles = [addtionnal_styles];
        }
        for (const file of addtionnal_styles) {
          stylesheet += `@import "${file}";`;
        }
        this.createStylesheet(stylesheet);
      }
    },
    getConfig: function (path = "/config.yml") {
      return fetch(path, { redirect: "manual" }).then((response) => {
        if (response.type === "opaqueredirect") {
          setTimeout(() => {
            if ('serviceWorker' in navigator) {
              navigator.serviceWorker.getRegistrations().then(function (registrations) {
                for (let registration of registrations) {
                  registration.unregister();
                }
                window.location.reload(true); // force reload after unregister
              });
            } else {
              window.location.reload(true);
            }
          }, 1000);
          return;
        }

        if (response.status == 404 || response.redirected) {
          this.configNotFound = true;
          return {};
        }

        if (!response.ok) {
          throw Error(`${response.statusText}: ${response.body}`);
        }

        const that = this;
        return response
          .text()
          .then((body) => {
            return parse(body, { merge: true });
          })
          .then(function (config) {
            if (config.externalConfig) {
              return that.getConfig(config.externalConfig);
            }
            return config;
          });
      });
    },
    matchesFilter: function (item) {
      const needle = this.filter?.toLowerCase();
      return (
        item.name.toLowerCase().includes(needle) ||
        (item.subtitle && item.subtitle.toLowerCase().includes(needle)) ||
        (item.tag && item.tag.toLowerCase().includes(needle)) ||
        (item.keywords && item.keywords.toLowerCase().includes(needle))
      );
    },
    navigateToFirstService: function (target) {
      try {
        const service = this.services[0].items[0];
        window.open(service.url, target || service.target || "_self");
      } catch {
        console.warn("fail to open service");
      }
    },
    filterServices: function (filter) {
      this.filter = filter;

      if (!filter) {
        this.services = this.config.services;
        return;
      }

      const searchResultItems = [];
      for (const group of this.config.services) {
        if (group.items !== null) {
          for (const item of group.items) {
            if (this.matchesFilter(item)) {
              searchResultItems.push(item);
            }
          }
        }
      }

      this.services = [
        {
          name: filter,
          icon: "fas fa-search",
          items: searchResultItems,
        },
      ];
    },
    handleErrors: function (title, content) {
      return {
        message: {
          title: title,
          style: "is-danger",
          content: content,
        },
      };
    },
    createStylesheet: function (css) {
      let style = document.createElement("style");
      style.appendChild(document.createTextNode(css));
      document.head.appendChild(style);
    },
  },
  beforeDestroy() {
    // Clear the config polling interval when component is destroyed
    if (this.configInterval) {
      clearInterval(this.configInterval);
      this.configInterval = null;
    }
  },
};
</script>
