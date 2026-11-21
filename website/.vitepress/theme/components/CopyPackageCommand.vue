<script setup>
import { ref } from 'vue'

const command = 'dotnet add package Titanium.Web.Proxy'
const copied = ref(false)

async function copy() {
  try {
    await navigator.clipboard.writeText(command)
    copied.value = true
    window.setTimeout(() => {
      copied.value = false
    }, 1600)
  } catch {
    // Selection still works if clipboard is denied.
  }
}
</script>

<template>
  <div class="copy-package">
    <code class="copy-package__cmd">{{ command }}</code>
    <button type="button" class="copy-package__btn" @click="copy">
      {{ copied ? 'Copied' : 'Copy' }}
    </button>
  </div>
</template>
