import {
  createApplicationMessage,
  createCommit,
  createGroup,
  decodeGroupState,
  decodeMlsMessage,
  defaultCapabilities,
  defaultLifetime,
  emptyPskIndex,
  encodeGroupState,
  encodeMlsMessage,
  generateKeyPackage,
  getCiphersuiteFromName,
  getCiphersuiteImpl,
  joinGroup,
  makePskIndex,
  processPrivateMessage,
  zeroOutUint8Array,
} from 'ts-mls';
import { defaultClientConfig } from 'ts-mls/clientConfig.js';

const DATABASE_NAME = 'jellyfin-chat-e2ee';
const DATABASE_VERSION = 1;
const STORE_NAME = 'records';
const CIPHERSUITE = 'MLS_128_DHKEMX25519_AES128GCM_SHA256_Ed25519';
const encoder = new TextEncoder();
const decoder = new TextDecoder();

function property(item, name, fallback) {
  if (!item) return fallback;
  const camel = name.charAt(0).toLowerCase() + name.slice(1);
  return item[name] ?? item[camel] ?? fallback;
}

function normalizeId(value) {
  return String(value || '').toLowerCase().replace(/[{}-]/g, '');
}

function bytesEqual(left, right) {
  if (!(left instanceof Uint8Array) || !(right instanceof Uint8Array) || left.length !== right.length) return false;
  let difference = 0;
  for (let index = 0; index < left.length; index += 1) difference |= left[index] ^ right[index];
  return difference === 0;
}

function toBase64(bytes) {
  let binary = '';
  for (let offset = 0; offset < bytes.length; offset += 0x8000) {
    binary += String.fromCharCode(...bytes.subarray(offset, offset + 0x8000));
  }
  return btoa(binary);
}

function fromBase64(value) {
  const binary = atob(value);
  const bytes = new Uint8Array(binary.length);
  for (let index = 0; index < binary.length; index += 1) bytes[index] = binary.charCodeAt(index);
  return bytes;
}

function decodeOne(decode, bytes, label) {
  const result = decode(bytes, 0);
  if (!result || result[1] !== bytes.length) throw new Error(`Invalid ${label}.`);
  return result[0];
}

function parseCredential(credential) {
  if (!credential || credential.credentialType !== 'basic') return null;
  try {
    const value = JSON.parse(decoder.decode(credential.identity));
    const userId = normalizeId(value.u);
    const deviceId = normalizeId(value.d);
    return userId && deviceId ? { userId, deviceId } : null;
  } catch (_) {
    return null;
  }
}

function openDatabase() {
  return new Promise((resolve, reject) => {
    const request = indexedDB.open(DATABASE_NAME, DATABASE_VERSION);
    request.onupgradeneeded = () => {
      if (!request.result.objectStoreNames.contains(STORE_NAME)) request.result.createObjectStore(STORE_NAME, { keyPath: 'key' });
    };
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error || new Error('Could not open encrypted chat storage.'));
  });
}

async function databaseOperation(mode, operation) {
  const database = await openDatabase();
  try {
    return await new Promise((resolve, reject) => {
      const transaction = database.transaction(STORE_NAME, mode);
      const store = transaction.objectStore(STORE_NAME);
      let result;
      try {
        result = operation(store);
      } catch (error) {
        reject(error);
        return;
      }
      transaction.oncomplete = () => resolve(result?.result);
      transaction.onerror = () => reject(transaction.error || new Error('Encrypted chat storage failed.'));
      transaction.onabort = () => reject(transaction.error || new Error('Encrypted chat storage was aborted.'));
    });
  } finally {
    database.close();
  }
}

const getRecord = key => databaseOperation('readonly', store => store.get(key));
const putRecord = record => databaseOperation('readwrite', store => store.put(record));
const deleteRecord = key => databaseOperation('readwrite', store => store.delete(key));
const getAllRecords = () => databaseOperation('readonly', store => store.getAll());

function privatePackageToRecord(privatePackage) {
  return {
    initPrivateKey: toBase64(privatePackage.initPrivateKey),
    hpkePrivateKey: toBase64(privatePackage.hpkePrivateKey),
    signaturePrivateKey: toBase64(privatePackage.signaturePrivateKey),
  };
}

function privatePackageFromRecord(record) {
  return {
    initPrivateKey: fromBase64(record.initPrivateKey),
    hpkePrivateKey: fromBase64(record.hpkePrivateKey),
    signaturePrivateKey: fromBase64(record.signaturePrivateKey),
  };
}

function browserLabel() {
  const platform = navigator.userAgentData?.platform || navigator.platform || 'Browser';
  return `Web · ${String(platform).slice(0, 60)}`;
}

function errorStatus(error) {
  return Number(error?.status ?? error?.statusCode ?? error?.response?.status ?? 0);
}

export class EncryptedChatClient {
  constructor(options) {
    this.api = options.api;
    this.userId = normalizeId(options.userId);
    this.userName = String(options.userName || 'Jellyfin user');
    this.messageLimit = Math.min(4000, Math.max(100, Number(options.messageLimit) || 1000));
    this.suitePromise = getCiphersuiteImpl(getCiphersuiteFromName(CIPHERSUITE));
    this.device = null;
    this.channelOperations = new Map();
    this.publishedChannels = new Set();
  }

  key(suffix) {
    return `${suffix}:${this.userId}`;
  }

  channelKey(kind, channelId) {
    return this.key(`${kind}:${normalizeId(channelId)}`);
  }

  async initialize() {
    if (!this.userId) throw new Error('The Jellyfin user identity is unavailable.');
    let record = await getRecord(this.key('device'));
    if (!record) {
      record = { key: this.key('device'), id: crypto.randomUUID(), label: browserLabel(), createdAtUtc: new Date().toISOString() };
      await putRecord(record);
    }

    this.device = record;
    await this.api('JellyfinChat/Crypto/Devices', {
      type: 'POST',
      data: { DeviceId: record.id, Label: record.label },
    });
    return { id: record.id, label: record.label };
  }

  async ensureInitialized() {
    if (!this.device) await this.initialize();
  }

  async ensureKeyPackage(channelId, publish = true, force = false) {
    await this.ensureInitialized();
    const normalizedChannel = normalizeId(channelId);
    const key = this.channelKey('key-package', normalizedChannel);
    let record = force ? null : await getRecord(key);
    if (!record) {
      const suite = await this.suitePromise;
      const credential = {
        credentialType: 'basic',
        identity: encoder.encode(JSON.stringify({ u: this.userId, d: normalizeId(this.device.id) })),
      };
      const generated = await generateKeyPackage(credential, defaultCapabilities(), defaultLifetime, [], suite);
      const payload = encodeMlsMessage({
        keyPackage: generated.publicPackage,
        wireformat: 'mls_key_package',
        version: 'mls10',
      });
      record = {
        key,
        payload: toBase64(payload),
        privatePackage: privatePackageToRecord(generated.privatePackage),
        createdAtUtc: new Date().toISOString(),
      };
      await putRecord(record);
    }

    if (publish && (force || !this.publishedChannels.has(normalizedChannel))) {
      await this.api(`JellyfinChat/Crypto/Channels/${encodeURIComponent(normalizedChannel)}/KeyPackages`, {
        type: 'POST',
        data: { DeviceId: this.device.id, Payload: record.payload },
      });
      this.publishedChannels.add(normalizedChannel);
    }
    return record;
  }

  async makeClientConfig(channelId, bootstrap) {
    const devices = new Map();
    for (const item of property(bootstrap, 'Devices', [])) devices.set(normalizeId(property(item, 'Id')), item);
    const pinPrefix = `${this.channelKey('trust', channelId)}:`;
    const pins = new Map(
      (await getAllRecords())
        .filter(record => record.key.startsWith(pinPrefix))
        .map(record => [normalizeId(record.deviceId), record.signaturePublicKey]),
    );
    const trusted = new Map();
    for (const item of property(bootstrap, 'KeyPackages', [])) {
      try {
        const message = decodeOne(decodeMlsMessage, fromBase64(property(item, 'Payload')), 'MLS key package');
        if (message.wireformat !== 'mls_key_package') continue;
        const identity = parseCredential(message.keyPackage.leafNode.credential);
        const deviceId = normalizeId(property(item, 'DeviceId'));
        const device = devices.get(deviceId);
        if (!identity || !device || identity.deviceId !== deviceId || identity.userId !== normalizeId(property(device, 'UserId'))) continue;
        const pinnedKey = pins.get(deviceId);
        if (pinnedKey && pinnedKey !== toBase64(message.keyPackage.leafNode.signaturePublicKey)) continue;
        trusted.set(`${identity.userId}|${identity.deviceId}`, message.keyPackage.leafNode.signaturePublicKey);
      } catch (_) {
        // Malformed or mismatched packages are deliberately excluded from trust.
      }
    }

    return {
      ...defaultClientConfig,
      authService: {
        validateCredential: async (credential, signaturePublicKey) => {
          const identity = parseCredential(credential);
          if (!identity) return false;
          const expected = trusted.get(`${identity.userId}|${identity.deviceId}`);
          return !!expected && bytesEqual(expected, signaturePublicKey);
        },
      },
    };
  }

  async loadGroupRecord(channelId) {
    return getRecord(this.channelKey('group', channelId));
  }

  decodeGroup(record, clientConfig) {
    if (!record?.state) return null;
    const decoded = decodeOne(decodeGroupState, fromBase64(record.state), 'MLS group state');
    return { ...decoded, clientConfig };
  }

  async saveGroup(channelId, state, previous = {}) {
    const record = {
      key: this.channelKey('group', channelId),
      state: toBase64(encodeGroupState(state)),
      lastSequence: Number(previous.lastSequence || 0),
      ownEventIds: Array.from(new Set((previous.ownEventIds || []).map(normalizeId))).slice(-256),
    };
    await putRecord(record);
    return record;
  }

  async fetchBootstrap(channelId, afterSequence) {
    return this.api(
      `JellyfinChat/Crypto/Channels/${encodeURIComponent(channelId)}?deviceId=${encodeURIComponent(this.device.id)}&afterSequence=${Math.max(0, Number(afterSequence) || 0)}&limit=200`,
    );
  }

  async cachedMessages(channelId) {
    const prefix = this.channelKey('message', channelId) + ':';
    return (await getAllRecords())
      .filter(record => record.key.startsWith(prefix))
      .map(record => record.message)
      .sort((left, right) => new Date(property(left, 'CreatedAtUtc')) - new Date(property(right, 'CreatedAtUtc')))
      .slice(-200);
  }

  async cacheMessage(channelId, event, content) {
    const id = normalizeId(property(event, 'Id'));
    await putRecord({
      key: `${this.channelKey('message', channelId)}:${id}`,
      message: {
        Id: property(event, 'Id'),
        ChannelId: property(event, 'ChannelId'),
        AuthorId: content.authorId,
        AuthorName: content.authorName,
        Body: content.body,
        CreatedAtUtc: property(event, 'CreatedAtUtc'),
        IsEncrypted: true,
      },
    });
  }

  async deleteCachedMessage(channelId, eventId) {
    await deleteRecord(`${this.channelKey('message', channelId)}:${normalizeId(eventId)}`);
  }

  async pruneCachedMessages(channelId, cutoffUtc) {
    const prefix = this.channelKey('message', channelId) + ':';
    const cutoff = new Date(cutoffUtc || 0).getTime();
    const records = (await getAllRecords())
      .filter(record => record.key.startsWith(prefix))
      .sort((left, right) => new Date(property(left.message, 'CreatedAtUtc')) - new Date(property(right.message, 'CreatedAtUtc')));
    const excess = Math.max(0, records.length - 200);
    for (let index = 0; index < records.length; index += 1) {
      const created = new Date(property(records[index].message, 'CreatedAtUtc')).getTime();
      if (index < excess || (Number.isFinite(cutoff) && created < cutoff)) await deleteRecord(records[index].key);
    }
  }

  memberEntries(state) {
    const entries = [];
    for (let nodeIndex = 0; nodeIndex < state.ratchetTree.length; nodeIndex += 2) {
      const node = state.ratchetTree[nodeIndex];
      if (!node || node.nodeType !== 'leaf') continue;
      const identity = parseCredential(node.leaf.credential);
      if (identity) entries.push({ ...identity, leafIndex: nodeIndex / 2, signaturePublicKey: node.leaf.signaturePublicKey });
    }
    return entries;
  }

  async verifyAndPinMembers(channelId, state) {
    for (const member of this.memberEntries(state)) {
      const key = `${this.channelKey('trust', channelId)}:${member.deviceId}`;
      const encoded = toBase64(member.signaturePublicKey);
      const pinned = await getRecord(key);
      if (pinned && pinned.signaturePublicKey !== encoded) {
        throw new Error('An encrypted-chat device identity changed. Verify the security code before continuing.');
      }
      if (!pinned) await putRecord({ key, deviceId: member.deviceId, userId: member.userId, signaturePublicKey: encoded });
    }
  }

  async securityCode(state) {
    const identities = this.memberEntries(state)
      .map(member => `${member.userId}|${member.deviceId}|${toBase64(member.signaturePublicKey)}`)
      .sort();
    const digest = new Uint8Array(await crypto.subtle.digest('SHA-256', encoder.encode(identities.join('\n'))));
    return Array.from(digest.subarray(0, 12), byte => byte.toString(16).padStart(2, '0'))
      .join('')
      .match(/.{1,4}/g)
      .join(' ');
  }

  async processEvents(channelId, bootstrap, state, record, clientConfig) {
    const suite = await this.suitePromise;
    const keyPackageRecord = await getRecord(this.channelKey('key-package', channelId));
    const events = property(bootstrap, 'Events', []);
    for (const event of events) {
      const sequence = Number(property(event, 'Sequence'));
      const kind = property(event, 'Kind');
      const eventId = normalizeId(property(event, 'Id'));
      if (sequence <= Number(record?.lastSequence || 0)) continue;

      if (kind === 'Welcome' && !state) {
        if (!keyPackageRecord) throw new Error('The private key package for this welcome is missing.');
        const welcomeMessage = decodeOne(decodeMlsMessage, fromBase64(property(event, 'Payload')), 'MLS welcome');
        const publicMessage = decodeOne(decodeMlsMessage, fromBase64(keyPackageRecord.payload), 'MLS key package');
        if (welcomeMessage.wireformat !== 'mls_welcome' || publicMessage.wireformat !== 'mls_key_package') throw new Error('Unexpected MLS welcome data.');
        state = await joinGroup(
          welcomeMessage.welcome,
          publicMessage.keyPackage,
          privatePackageFromRecord(keyPackageRecord.privatePackage),
          emptyPskIndex,
          suite,
          undefined,
          undefined,
          clientConfig,
        );
        await this.verifyAndPinMembers(channelId, state);
      } else if (kind === 'Commit' && state) {
        if (!(record?.ownEventIds || []).some(id => normalizeId(id) === eventId)) {
          const message = decodeOne(decodeMlsMessage, fromBase64(property(event, 'Payload')), 'MLS commit');
          if (message.wireformat !== 'mls_private_message') throw new Error('Unexpected MLS commit format.');
          const result = await processPrivateMessage(state, message.privateMessage, makePskIndex(state, {}), suite);
          try {
            if (result.kind !== 'newState') throw new Error('Expected an MLS group-state update.');
            await this.verifyAndPinMembers(channelId, result.newState);
            state = result.newState;
          } finally {
            result.consumed.forEach(zeroOutUint8Array);
          }
        }
      } else if (kind === 'Application' && state) {
        if (property(event, 'Payload')) {
          if (!(record?.ownEventIds || []).some(id => normalizeId(id) === eventId)) {
            const message = decodeOne(decodeMlsMessage, fromBase64(property(event, 'Payload')), 'MLS application message');
            if (message.wireformat !== 'mls_private_message') throw new Error('Unexpected MLS application format.');
            const result = await processPrivateMessage(state, message.privateMessage, makePskIndex(state, {}), suite);
            try {
              if (result.kind !== 'applicationMessage') throw new Error('Expected an MLS application message.');
              state = result.newState;
              const content = JSON.parse(decoder.decode(result.message));
              if (normalizeId(content.id) !== eventId
                || normalizeId(content.channelId) !== normalizeId(channelId)
                || normalizeId(content.authorId) !== normalizeId(property(event, 'AuthorId'))
                || content.authorName !== String(property(event, 'AuthorName') || '')
                || normalizeId(content.deviceId) !== normalizeId(property(event, 'SenderDeviceId'))
                || typeof content.authorName !== 'string'
                || typeof content.body !== 'string'
                || content.body.length > this.messageLimit) {
                throw new Error('Encrypted message metadata did not match its envelope.');
              }
              await this.cacheMessage(channelId, event, content);
            } finally {
              result.consumed.forEach(zeroOutUint8Array);
            }
          }
        } else {
          await this.deleteCachedMessage(channelId, eventId);
        }
      } else if (kind === 'Delete') {
        await this.deleteCachedMessage(channelId, property(event, 'TargetEventId'));
      }

      if (!record) record = { key: this.channelKey('group', channelId), ownEventIds: [] };
      record.lastSequence = sequence;
      record.ownEventIds = (record.ownEventIds || []).map(normalizeId).filter(id => id !== eventId);
      if (state) record = await this.saveGroup(channelId, state, record);
      else await putRecord(record);
    }
    return { state, record };
  }

  async reconcile(channelId, bootstrap, state, record) {
    if (!state || state.groupActiveState?.kind !== 'active') return { state, record, changed: false };
    const serverEpoch = Number(property(bootstrap, 'Epoch'));
    if (Number(state.groupContext.epoch) !== serverEpoch) return { state, record, changed: false };
    const current = this.memberEntries(state);
    const currentIds = new Set(current.map(item => item.deviceId));
    const target = property(bootstrap, 'TargetMemberDeviceIds', []).map(normalizeId);
    const targetIds = new Set(target);
    const additions = target.filter(id => !currentIds.has(id));
    const removals = current.filter(item => !targetIds.has(item.deviceId));
    if (!additions.length && !removals.length) return { state, record, changed: false };

    const packages = new Map(property(bootstrap, 'PendingKeyPackages', []).map(item => [normalizeId(property(item, 'DeviceId')), property(item, 'Payload')]));
    const proposals = removals.map(item => ({ proposalType: 'remove', remove: { removed: item.leafIndex } }));
    for (const deviceId of additions) {
      const payload = packages.get(deviceId);
      if (!payload) return { state, record, changed: false };
      const message = decodeOne(decodeMlsMessage, fromBase64(payload), 'MLS key package');
      if (message.wireformat !== 'mls_key_package') throw new Error('Unexpected MLS key package format.');
      proposals.push({ proposalType: 'add', add: { keyPackage: message.keyPackage } });
    }

    const suite = await this.suitePromise;
    const result = await createCommit(
      { state, cipherSuite: suite },
      { extraProposals: proposals, ratchetTreeExtension: true },
    );
    await this.verifyAndPinMembers(channelId, result.newState);
    const eventId = crypto.randomUUID();
    const payload = toBase64(encodeMlsMessage(result.commit));
    const welcomePayload = result.welcome
      ? toBase64(encodeMlsMessage({ welcome: result.welcome, wireformat: 'mls_welcome', version: 'mls10' }))
      : null;
    const request = {
      EventId: eventId,
      DeviceId: this.device.id,
      ExpectedEpoch: serverEpoch,
      Payload: payload,
      MemberDeviceIds: target,
      Welcomes: additions.map(deviceId => ({ RecipientDeviceId: deviceId, Payload: welcomePayload })),
    };
    const oldRecord = { ...record };
    const newRecord = await this.saveGroup(channelId, result.newState, {
      ...record,
      ownEventIds: [...(record.ownEventIds || []), normalizeId(eventId)],
    });
    const pendingKey = this.channelKey('pending-commit', channelId);
    await putRecord({ key: pendingKey, request, oldRecord, newRecord });
    try {
      await this.api(`JellyfinChat/Crypto/Channels/${encodeURIComponent(channelId)}/Commits`, { type: 'POST', data: request });
      await deleteRecord(pendingKey);
      return { state: result.newState, record: newRecord, changed: true };
    } catch (error) {
      if (errorStatus(error) === 409) {
        await putRecord(oldRecord);
        await deleteRecord(pendingKey);
        return { state, record: oldRecord, changed: false };
      }
      throw error;
    } finally {
      result.consumed.forEach(zeroOutUint8Array);
    }
  }

  async flushPending(channelId) {
    const commitKey = this.channelKey('pending-commit', channelId);
    const pendingCommit = await getRecord(commitKey);
    if (pendingCommit) {
      try {
        await this.api(`JellyfinChat/Crypto/Channels/${encodeURIComponent(channelId)}/Commits`, { type: 'POST', data: pendingCommit.request });
        await deleteRecord(commitKey);
      } catch (error) {
        if (errorStatus(error) === 409) {
          await putRecord(pendingCommit.oldRecord);
          await deleteRecord(commitKey);
        } else {
          throw error;
        }
      }
    }

    const prefix = this.channelKey('pending-message', channelId) + ':';
    for (const pending of (await getAllRecords()).filter(record => record.key.startsWith(prefix))) {
      try {
        const event = await this.api(`JellyfinChat/Crypto/Channels/${encodeURIComponent(channelId)}/Messages`, { type: 'POST', data: pending.request });
        await this.cacheMessage(channelId, event, pending.content);
        await deleteRecord(pending.key);
      } catch (error) {
        if (errorStatus(error) === 409) {
          if (pending.oldRecord) await putRecord(pending.oldRecord);
          await deleteRecord(pending.key);
        } else {
          throw error;
        }
      }
    }
  }

  async syncInternal(channelId) {
    await this.ensureInitialized();
    await this.flushPending(channelId);
    let record = await this.loadGroupRecord(channelId);
    if (!record) await this.ensureKeyPackage(channelId, true);
    let bootstrap = await this.fetchBootstrap(channelId, Number(record?.lastSequence || 0));
    await this.pruneCachedMessages(channelId, property(bootstrap, 'RetentionCutoffUtc'));
    let clientConfig = await this.makeClientConfig(channelId, bootstrap);
    let state = record ? this.decodeGroup(record, clientConfig) : null;
    if (state) await this.verifyAndPinMembers(channelId, state);

    if (!property(bootstrap, 'GroupExists')) {
      if (record) {
        await deleteRecord(record.key);
        record = null;
        state = null;
      }
      const serverHasOwnPackage = property(bootstrap, 'KeyPackages', [])
        .some(item => normalizeId(property(item, 'DeviceId')) === normalizeId(this.device.id));
      if (!serverHasOwnPackage) this.publishedChannels.delete(normalizeId(channelId));
      const keyPackageRecord = await this.ensureKeyPackage(channelId, true);
      const packageMessage = decodeOne(decodeMlsMessage, fromBase64(keyPackageRecord.payload), 'MLS key package');
      if (packageMessage.wireformat !== 'mls_key_package') throw new Error('Unexpected MLS key package format.');
      const suite = await this.suitePromise;
      const candidate = await createGroup(
        encoder.encode(`jellyfin-chat:${normalizeId(channelId)}`),
        packageMessage.keyPackage,
        privatePackageFromRecord(keyPackageRecord.privatePackage),
        [],
        suite,
        clientConfig,
      );
      await this.verifyAndPinMembers(channelId, candidate);
      try {
        await this.api(`JellyfinChat/Crypto/Channels/${encodeURIComponent(channelId)}/Claim`, {
          type: 'POST',
          data: { DeviceId: this.device.id },
        });
        state = candidate;
        record = await this.saveGroup(channelId, state, { lastSequence: 0, ownEventIds: [] });
      } catch (error) {
        if (errorStatus(error) !== 409) throw error;
      }
      bootstrap = await this.fetchBootstrap(channelId, Number(record?.lastSequence || 0));
      clientConfig = await this.makeClientConfig(channelId, bootstrap);
      if (state) state.clientConfig = clientConfig;
    }

    for (let page = 0; page < 20; page += 1) {
      const processed = await this.processEvents(channelId, bootstrap, state, record, clientConfig);
      state = processed.state;
      record = processed.record;
      const count = property(bootstrap, 'Events', []).length;
      if (count < 200) break;
      bootstrap = await this.fetchBootstrap(channelId, Number(record?.lastSequence || 0));
      clientConfig = await this.makeClientConfig(channelId, bootstrap);
      if (state) state.clientConfig = clientConfig;
    }

    if (!state) {
      return { status: 'waiting', messages: await this.cachedMessages(channelId), deviceId: this.device.id };
    }
    if (state.groupActiveState?.kind !== 'active') {
      await deleteRecord(this.channelKey('group', channelId));
      await this.ensureKeyPackage(channelId, true, true);
      return { status: 'waiting', messages: await this.cachedMessages(channelId), deviceId: this.device.id };
    }

    const reconciled = await this.reconcile(channelId, bootstrap, state, record);
    state = reconciled.state;
    record = reconciled.record;
    return {
      status: 'ready',
      messages: await this.cachedMessages(channelId),
      deviceId: this.device.id,
      epoch: Number(state.groupContext.epoch),
      memberCount: this.memberEntries(state).length,
      allParticipantsReady: Boolean(property(bootstrap, 'AllParticipantsReady')),
      securityCode: await this.securityCode(state),
      changed: reconciled.changed,
    };
  }

  async runChannelOperation(channelId, operation) {
    const normalized = normalizeId(channelId);
    const previous = this.channelOperations.get(normalized) || Promise.resolve();
    const current = previous.catch(() => {}).then(operation);
    this.channelOperations.set(normalized, current);
    try {
      return await current;
    } finally {
      if (this.channelOperations.get(normalized) === current) this.channelOperations.delete(normalized);
    }
  }

  async sync(channelId) {
    const normalized = normalizeId(channelId);
    return this.runChannelOperation(normalized, () => this.syncInternal(normalized));
  }

  async send(channelId, body) {
    const normalizedChannel = normalizeId(channelId);
    const text = String(body || '').trim();
    if (!text) throw new Error('A message is required.');
    if (text.length > this.messageLimit) throw new Error(`Messages cannot exceed ${this.messageLimit} characters.`);
    return this.runChannelOperation(normalizedChannel, () => this.sendInternal(normalizedChannel, text));
  }

  async sendInternal(normalizedChannel, text) {
    const synced = await this.syncInternal(normalizedChannel);
    if (synced.status !== 'ready') throw new Error('Waiting for the secure channel key.');
    let record = await this.loadGroupRecord(normalizedChannel);
    const bootstrap = await this.fetchBootstrap(normalizedChannel, Number(record?.lastSequence || 0));
    const clientConfig = await this.makeClientConfig(normalizedChannel, bootstrap);
    const state = this.decodeGroup(record, clientConfig);
    if (!state || Number(state.groupContext.epoch) !== Number(property(bootstrap, 'Epoch'))) throw new Error('The secure channel is synchronizing.');
    const eventId = crypto.randomUUID();
    const content = {
      id: normalizeId(eventId),
      channelId: normalizedChannel,
      authorId: this.userId,
      authorName: this.userName,
      deviceId: normalizeId(this.device.id),
      body: text,
    };
    const suite = await this.suitePromise;
    const encrypted = await createApplicationMessage(
      state,
      encoder.encode(JSON.stringify(content)),
      suite,
    );
    const oldRecord = { ...record, ownEventIds: [...(record.ownEventIds || [])] };
    const request = {
      EventId: eventId,
      DeviceId: this.device.id,
      Epoch: Number(state.groupContext.epoch),
      Payload: toBase64(encodeMlsMessage({
        privateMessage: encrypted.privateMessage,
        wireformat: 'mls_private_message',
        version: 'mls10',
      })),
    };
    record = await this.saveGroup(normalizedChannel, encrypted.newState, {
      ...record,
      ownEventIds: [...(record.ownEventIds || []), normalizeId(eventId)],
    });
    const pending = {
      key: `${this.channelKey('pending-message', normalizedChannel)}:${normalizeId(eventId)}`,
      request,
      content,
      oldRecord,
    };
    await putRecord(pending);
    try {
      const event = await this.api(`JellyfinChat/Crypto/Channels/${encodeURIComponent(normalizedChannel)}/Messages`, { type: 'POST', data: request });
      await this.cacheMessage(normalizedChannel, event, content);
      await deleteRecord(pending.key);
      return event;
    } catch (error) {
      if (errorStatus(error) === 409) {
        await putRecord(oldRecord);
        await deleteRecord(pending.key);
      }
      throw error;
    } finally {
      encrypted.consumed.forEach(zeroOutUint8Array);
    }
  }

  async deleteMessage(eventId) {
    await this.api(`JellyfinChat/Crypto/Messages/${encodeURIComponent(eventId)}`, { type: 'DELETE' });
  }
}

export async function createClient(options) {
  if (globalThis.isSecureContext === false || !globalThis.crypto?.subtle) {
    const error = new Error('End-to-end encryption requires HTTPS or localhost.');
    error.code = 'secure-context-required';
    throw error;
  }
  if (!globalThis.indexedDB) {
    const error = new Error('This browser does not provide secure local chat storage.');
    error.code = 'secure-storage-unavailable';
    throw error;
  }
  const client = new EncryptedChatClient(options);
  await client.initialize();
  return client;
}

export const protocol = Object.freeze({
  name: 'MLS 1.0',
  ciphersuite: CIPHERSUITE,
  experimental: true,
});
