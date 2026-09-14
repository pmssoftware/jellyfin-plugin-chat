import assert from 'node:assert/strict';

function installMemoryIndexedDb() {
  const databases = new Map();

  class MemoryDatabase {
    constructor() {
      this.stores = new Map();
      this.objectStoreNames = { contains: name => this.stores.has(name) };
    }

    createObjectStore(name) {
      this.stores.set(name, new Map());
    }

    transaction(name) {
      const transaction = {};
      const records = this.stores.get(name);
      const request = result => ({ result });
      const store = {
        get: key => request(structuredClone(records.get(key))),
        getAll: () => request(Array.from(records.values(), value => structuredClone(value))),
        put: record => {
          records.set(record.key, structuredClone(record));
          return request(record.key);
        },
        delete: key => {
          records.delete(key);
          return request(undefined);
        },
      };
      transaction.objectStore = () => store;
      queueMicrotask(() => transaction.oncomplete?.());
      return transaction;
    }

    close() {}
  }

  globalThis.indexedDB = {
    open(name) {
      const openRequest = {};
      queueMicrotask(() => {
        const created = !databases.has(name);
        if (created) databases.set(name, new MemoryDatabase());
        openRequest.result = databases.get(name);
        if (created) openRequest.onupgradeneeded?.();
        openRequest.onsuccess?.();
      });
      return openRequest;
    },
  };
}

installMemoryIndexedDb();
Object.defineProperty(globalThis, 'navigator', {
  value: { platform: 'Test browser' },
  configurable: true,
});

const { createClient } = await import('../web-src/e2ee-client.js');
const channelId = crypto.randomUUID();
const users = {
  alice: { id: crypto.randomUUID(), name: 'Alice' },
  bob: { id: crypto.randomUUID(), name: 'Bob' },
};

function createServer() {
  const devices = new Map();
  const keyPackages = new Map();
  const events = [];
  let group = null;
  let nextSequence = 1;
  let keyPackagePosts = 0;
  let rejectNextMessage = false;

  function conflict(message) {
    const error = new Error(message);
    error.status = 409;
    throw error;
  }

  function addEvent(event) {
    const stored = { ...event, Sequence: nextSequence, CreatedAtUtc: new Date().toISOString() };
    nextSequence += 1;
    events.push(stored);
    return structuredClone(stored);
  }

  function apiFor(user) {
    return async (path, options = {}) => {
      const method = options.type || 'GET';
      const parsed = new URL(path, 'https://jellyfin.test/');
      const route = parsed.pathname.replace(/^\//, '');

      if (route === 'JellyfinChat/Crypto/Devices' && method === 'POST') {
        const id = options.data.DeviceId.toLowerCase();
        devices.set(id, {
          Id: id,
          UserId: user.id,
          UserName: user.name,
          Label: options.data.Label,
        });
        return structuredClone(devices.get(id));
      }

      const keyPackageMatch = route.match(/^JellyfinChat\/Crypto\/Channels\/([^/]+)\/KeyPackages$/);
      if (keyPackageMatch && method === 'POST') {
        keyPackagePosts += 1;
        assert.equal(keyPackageMatch[1], channelId);
        const deviceId = options.data.DeviceId.toLowerCase();
        const existing = keyPackages.get(deviceId);
        if (group?.members.includes(deviceId) && existing?.consumed) return structuredClone(existing);
        const item = { DeviceId: deviceId, Payload: options.data.Payload, consumed: false };
        keyPackages.set(deviceId, item);
        return structuredClone(item);
      }

      const bootstrapMatch = route.match(/^JellyfinChat\/Crypto\/Channels\/([^/]+)$/);
      if (bootstrapMatch && method === 'GET') {
        assert.equal(bootstrapMatch[1], channelId);
        const deviceId = parsed.searchParams.get('deviceId').toLowerCase();
        const after = Number(parsed.searchParams.get('afterSequence') || 0);
        const members = group?.members || [];
        const pending = Array.from(keyPackages.values()).filter(item => !item.consumed);
        const target = Array.from(new Set([...members, ...pending.map(item => item.DeviceId)])).sort();
        return {
          GroupExists: !!group,
          Epoch: group?.epoch || 0,
          MemberDeviceIds: members,
          TargetMemberDeviceIds: target,
          Devices: Array.from(devices.values()),
          KeyPackages: Array.from(keyPackages.values()),
          PendingKeyPackages: pending,
          Events: events.filter(event => event.Sequence > after && event.AudienceDeviceIds.includes(deviceId)).slice(0, 200),
        };
      }

      const claimMatch = route.match(/^JellyfinChat\/Crypto\/Channels\/([^/]+)\/Claim$/);
      if (claimMatch && method === 'POST') {
        assert.equal(claimMatch[1], channelId);
        if (group) conflict('Already claimed.');
        const deviceId = options.data.DeviceId.toLowerCase();
        keyPackages.get(deviceId).consumed = true;
        group = { epoch: 0, members: [deviceId] };
        return { ChannelId: channelId, Epoch: 0, MemberDeviceIds: [deviceId] };
      }

      const commitMatch = route.match(/^JellyfinChat\/Crypto\/Channels\/([^/]+)\/Commits$/);
      if (commitMatch && method === 'POST') {
        assert.equal(commitMatch[1], channelId);
        const input = options.data;
        const duplicate = events.find(event => event.Id === input.EventId);
        if (duplicate) return structuredClone(duplicate);
        if (!group || group.epoch !== input.ExpectedEpoch) conflict('Epoch changed.');
        const previous = [...group.members];
        const requested = input.MemberDeviceIds.map(id => id.toLowerCase()).sort();
        const added = requested.filter(id => !previous.includes(id));
        const commit = addEvent({
          Id: input.EventId,
          ChannelId: channelId,
          Epoch: group.epoch + 1,
          Kind: 'Commit',
          SenderDeviceId: input.DeviceId.toLowerCase(),
          AuthorId: user.id,
          AuthorName: user.name,
          Payload: input.Payload,
          AudienceDeviceIds: previous,
        });
        for (const addedDeviceId of added) {
          const welcome = input.Welcomes.find(item => item.RecipientDeviceId.toLowerCase() === addedDeviceId);
          keyPackages.get(addedDeviceId).consumed = true;
          addEvent({
            Id: crypto.randomUUID(),
            ChannelId: channelId,
            Epoch: group.epoch + 1,
            Kind: 'Welcome',
            SenderDeviceId: input.DeviceId.toLowerCase(),
            RecipientDeviceId: addedDeviceId,
            AuthorId: user.id,
            AuthorName: user.name,
            Payload: welcome.Payload,
            AudienceDeviceIds: [addedDeviceId],
          });
        }
        group = { epoch: group.epoch + 1, members: requested };
        return commit;
      }

      const messageMatch = route.match(/^JellyfinChat\/Crypto\/Channels\/([^/]+)\/Messages$/);
      if (messageMatch && method === 'POST') {
        assert.equal(messageMatch[1], channelId);
        const input = options.data;
        if (rejectNextMessage) {
          rejectNextMessage = false;
          conflict('Injected message conflict.');
        }
        const duplicate = events.find(event => event.Id === input.EventId);
        if (duplicate) return structuredClone(duplicate);
        if (!group || group.epoch !== input.Epoch) conflict('Epoch changed.');
        return addEvent({
          Id: input.EventId,
          ChannelId: channelId,
          Epoch: input.Epoch,
          Kind: 'Application',
          SenderDeviceId: input.DeviceId.toLowerCase(),
          AuthorId: user.id,
          AuthorName: user.name,
          Payload: input.Payload,
          AudienceDeviceIds: [...group.members],
        });
      }

      throw new Error(`Unexpected test request: ${method} ${route}`);
    };
  }

  return {
    apiFor,
    events,
    rejectOneMessage() { rejectNextMessage = true; },
    get keyPackagePosts() { return keyPackagePosts; },
  };
}

const server = createServer();
const alice = await createClient({ api: server.apiFor(users.alice), userId: users.alice.id, userName: users.alice.name });
const bob = await createClient({ api: server.apiFor(users.bob), userId: users.bob.id, userName: users.bob.name });

assert.equal((await alice.sync(channelId)).status, 'ready');
assert.equal((await bob.sync(channelId)).status, 'waiting');
const aliceAfterHandshake = await alice.sync(channelId);
assert.equal(aliceAfterHandshake.status, 'ready');
assert.equal(aliceAfterHandshake.memberCount, 2);
const bobAfterHandshake = await bob.sync(channelId);
assert.equal(bobAfterHandshake.status, 'ready');
assert.equal(bobAfterHandshake.securityCode, aliceAfterHandshake.securityCode);

server.rejectOneMessage();
await assert.rejects(() => alice.send(channelId, 'rejected secret'));
await alice.send(channelId, 'alpha secret');
const bobReceived = await bob.sync(channelId);
assert.equal(bobReceived.messages.at(-1).Body, 'alpha secret');
assert.equal(bobReceived.messages.at(-1).AuthorName, 'Alice');

await bob.send(channelId, 'bob answer');
const aliceReceived = await alice.sync(channelId);
assert.deepEqual(aliceReceived.messages.map(message => message.Body), ['alpha secret', 'bob answer']);
assert.equal(server.events.some(event => event.Payload.includes('alpha secret') || event.Payload.includes('bob answer')), false);
assert.equal(server.keyPackagePosts, 2, 'Each browser should publish its channel key package only once.');

console.log('Encrypted client handshake and two-way messaging passed.');
