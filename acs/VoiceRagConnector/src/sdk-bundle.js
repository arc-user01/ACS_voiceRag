import { CallClient } from '@azure/communication-calling';
import { AzureCommunicationTokenCredential } from '@azure/communication-common';

console.log('📦 Loading local ACS SDK bundle...');

// Expose to global scope for index.html usage
window.CallClient = CallClient;
window.AzureCommunicationTokenCredential = AzureCommunicationTokenCredential;

console.log('✅ ACS SDKs attached to window object.');
